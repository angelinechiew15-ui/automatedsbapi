using AutomatedSb.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Controllers;

// Backend for the "Service Bundle" (charts) page. Provides the cascading
// dropdown data (SB names by owner) and the dashboard metadata (SB name,
// client corridors and labs) used to build the embedded Tableau views.
[ApiController]
[Route("api/service-bundle")]
public class ServiceBundleController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;            // CM matrix DB (mp12)
    private readonly IOracleRealisConnectionFactory _realisFactory; // REALIS DB (real12)
    private readonly ILogger<ServiceBundleController> _logger;

    // CLOC ids that are placeholders, not real labs (mirrors the legacy app).
    private static readonly decimal[] ExcludedClocs = { 9900m, 9901m };

    public ServiceBundleController(
        IOracleConnectionFactory factory,
        IOracleRealisConnectionFactory realisFactory,
        ILogger<ServiceBundleController> logger)
    {
        _factory = factory;
        _realisFactory = realisFactory;
        _logger = logger;
    }

    // GET api/service-bundle/sb-names?ownerId=123
    // Returns the service bundles for the given owner (person id). When ownerId
    // is empty or "All SB Owner", every valid service bundle is returned.
    [HttpGet("sb-names")]
    public async Task<ActionResult> GetSbNames([FromQuery] string? ownerId)
    {
        var all = string.IsNullOrWhiteSpace(ownerId) ||
                  string.Equals(ownerId, "All SB Owner", StringComparison.OrdinalIgnoreCase);

        var sql = all
            ? @"SELECT DISTINCT sb.cm_matrix_sb_id   AS value,
                                sb.cm_matrix_sb_name AS text
                  FROM cm_matrix_sb sb
                  JOIN cm_matrix_person_to_sb i
                    ON sb.cm_matrix_sb_id = i.cm_matrix_person_to_sb_sb_id
                 WHERE sb.cm_matrix_sb_valid = 'Y'
                 ORDER BY sb.cm_matrix_sb_name ASC"
            : @"SELECT DISTINCT sb.cm_matrix_sb_id   AS value,
                                sb.cm_matrix_sb_name AS text
                  FROM cm_matrix_sb sb
                  JOIN cm_matrix_person_to_sb i
                    ON sb.cm_matrix_sb_id = i.cm_matrix_person_to_sb_sb_id
                 WHERE i.cm_matrix_person_to_sb_person_id = :ownerId
                   AND sb.cm_matrix_sb_valid = 'Y'
                 ORDER BY sb.cm_matrix_sb_name ASC";

        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            if (!all)
            {
                cmd.Parameters.Add(new OracleParameter("ownerId", ownerId));
            }

            await using var reader = await cmd.ExecuteReaderAsync();
            var result = new List<object>();
            while (await reader.ReadAsync())
            {
                result.Add(new
                {
                    value = reader["value"]?.ToString() ?? "",
                    text = reader["text"]?.ToString() ?? ""
                });
            }

            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "GetSbNames failed for ownerId={OwnerId}", ownerId);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // GET api/service-bundle/dashboard?sbId=123
    // Returns the data needed to build the embedded Tableau charts for a SB:
    //   - sbName: used as the SB filter in the Tableau URLs
    //   - clientCorridors: used for the Pareto (CLIENT_CORRIDOR) chart
    //   - labs: drives the per-lab tabs (LOC filter)
    [HttpGet("dashboard")]
    public async Task<ActionResult> GetDashboard([FromQuery] string sbId)
    {
        if (string.IsNullOrWhiteSpace(sbId))
        {
            return BadRequest(new { success = false, message = "sbId is required." });
        }

        try
        {
            string sbName = "";
            var clientCorridors = new List<string>();
            var clocIds = new List<decimal>();

            await using (var conn = _factory.Create())
            {
                await conn.OpenAsync();

                // SB name
                await using (var cmd = new OracleCommand(
                    "SELECT cm_matrix_sb_name FROM cm_matrix_sb WHERE cm_matrix_sb_id = :sbId", conn)
                    { BindByName = true })
                {
                    cmd.Parameters.Add(new OracleParameter("sbId", sbId));
                    var scalar = await cmd.ExecuteScalarAsync();
                    sbName = scalar == null || scalar == DBNull.Value ? "" : scalar.ToString() ?? "";
                }

                // Client corridors + lab cloc ids for the SB
                await using (var cmd = new OracleCommand(
                    @"SELECT DISTINCT c.cm_matrix_client_corridor        AS cc,
                                      c.cm_matrix_client_lab_realis_cloc AS loc
                        FROM cm_matrix_client c
                        JOIN cm_matrix_sb sb ON sb.cm_matrix_sb_id = c.cm_matrix_client_sb_id
                       WHERE sb.cm_matrix_sb_id = :sbId
                         AND sb.cm_matrix_sb_valid = 'Y'", conn)
                    { BindByName = true })
                {
                    cmd.Parameters.Add(new OracleParameter("sbId", sbId));
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var cc = reader["cc"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(cc) && !clientCorridors.Contains(cc))
                        {
                            clientCorridors.Add(cc);
                        }

                        if (reader["loc"] != DBNull.Value)
                        {
                            var cloc = Convert.ToDecimal(reader["loc"]);
                            if (!ExcludedClocs.Contains(cloc) && !clocIds.Contains(cloc))
                            {
                                clocIds.Add(cloc);
                            }
                        }
                    }
                }
            }

            // Resolve cloc ids -> location names from the REALIS DB.
            var labs = await ResolveLabsAsync(clocIds);

            return Ok(new
            {
                success = true,
                sbId,
                sbName,
                clientCorridors,
                labs
            });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "GetDashboard failed for sbId={SbId}", sbId);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    private async Task<List<object>> ResolveLabsAsync(IReadOnlyList<decimal> clocIds)
    {
        var labs = new List<object>();
        if (clocIds.Count == 0)
        {
            return labs;
        }

        var binds = clocIds.Select((_, idx) => $":c{idx}").ToArray();
        var sql = $@"SELECT g.cloc_id, g.cloc_location_name
                       FROM ctlg_location_type g
                      WHERE g.cloc_id IN ({string.Join(", ", binds)})
                      ORDER BY g.cloc_location_name ASC";

        await using var conn = _realisFactory.Create();
        await conn.OpenAsync();
        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        for (var i = 0; i < clocIds.Count; i++)
        {
            cmd.Parameters.Add(new OracleParameter($"c{i}", clocIds[i]));
        }

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            labs.Add(new
            {
                value = reader["cloc_id"]?.ToString() ?? "",
                text = reader["cloc_location_name"]?.ToString() ?? ""
            });
        }

        return labs;
    }

    // GET api/service-bundle/charts?sbId=189&horizon=26-06&loc=RPT CENTRAL
    // Returns the data series used to draw the native charts. The actual SQL for
    // each series is filled in below once the source views/columns are provided.
    [HttpGet("charts")]
    public async Task<ActionResult> GetCharts(
        [FromQuery] string sbId,
        [FromQuery] string horizon,
        [FromQuery] string? loc)
    {
        if (string.IsNullOrWhiteSpace(sbId))
        {
            return BadRequest(new { success = false, message = "sbId is required." });
        }

        try
        {
            // The view filters by SB *name*, so resolve it from the id first.
            var sbName = await GetSbNameAsync(sbId);
            if (string.IsNullOrEmpty(sbName))
            {
                var empty = new List<object>();
                return Ok(new
                {
                    success = true,
                    tsDemand = empty, tsActual = empty,
                    rtuDemand = empty, rtuActual = empty,
                    costDemand = empty, costActual = empty,
                    pareto = empty,
                    tsRows = empty, rtuRows = empty, costRows = empty
                });
            }

            var tsDemand = await QueryTsDemandAsync(sbName, horizon, loc);
            var tsActual = await QueryTsActualAsync(sbName, horizon, loc);
            var rtuDemand = await QueryRtuDemandAsync(sbName, horizon, loc);
            var rtuActual = await QueryRtuActualAsync(sbName, horizon, loc);
            var costDemand = await QueryCostDemandAsync(sbName, horizon, loc);
            var costActual = await QueryCostActualAsync(sbName, horizon, loc);
            var pareto = await QueryParetoAsync(sbId, sbName, horizon, loc);

            // Detailed per-location breakdown (only meaningful when a single location
            // is selected; the "All" tab keeps the simple combined table).
            var (tsRows, rtuRows, costRows) = string.IsNullOrWhiteSpace(loc)
                ? (new List<object>(), new List<object>(), new List<object>())
                : await QueryBreakdownAsync(sbName, horizon, loc);

            return Ok(new
            {
                success = true,
                tsDemand,
                tsActual,
                rtuDemand,
                rtuActual,
                costDemand,
                costActual,
                pareto,
                tsRows,
                rtuRows,
                costRows
            });
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "GetCharts failed for sbId={SbId}", sbId);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    private async Task<string> GetSbNameAsync(string sbId)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync();
        await using var cmd = new OracleCommand(
            "SELECT cm_matrix_sb_name FROM cm_matrix_sb WHERE cm_matrix_sb_id = :sbId", conn)
            { BindByName = true };
        cmd.Parameters.Add(new OracleParameter("sbId", sbId));
        var scalar = await cmd.ExecuteScalarAsync();
        return scalar == null || scalar == DBNull.Value ? "" : scalar.ToString() ?? "";
    }

    // Builds a time-series SQL over the base table for the given measure plus its
    // adder/change value, grouped by fiscal quarter. The adder/change comes from
    // RPT.CM_MATRIX_SB_ADDER (the same join the v_sb_asb_data view uses), matched on
    // SB + location + horizon + fiscal quarter and filtered by type ('Adder' or
    // 'Change') and measure ('TS', 'RTU' or 'COST'). adderFor/adderType are constant,
    // code-controlled literals (not user input). Every numeric cast uses
    // DEFAULT 0 ON CONVERSION ERROR to avoid ORA-01722 on dirty text columns — the
    // reason we query the base table here instead of the view (which casts unsafely).
    private static string SeriesSql(string baseMeasure, string adderFor, string adderType, string? loc)
    {
        var locClause = string.IsNullOrWhiteSpace(loc) ? "" : " AND t.loc = :loc";
        return $@"SELECT CASE WHEN t.quarter IS NULL THEN t.fy ELSE t.fy || ' ' || t.quarter END AS label,
                         SUM(TO_NUMBER({baseMeasure} DEFAULT 0 ON CONVERSION ERROR)
                             + NVL(TO_NUMBER(a.cm_matrix_adder_value DEFAULT 0 ON CONVERSION ERROR), 0)) AS value
                    FROM rpt.asb_ts_actual t
                    LEFT JOIN rpt.cm_matrix_sb_adder a
                      ON t.loc = a.cm_matrix_adder_location
                     AND t.sb = a.cm_matrix_adder_sb_name
                     AND t.fy || '-' || t.quarter = a.cm_matrix_adder_fy || '-' || a.cm_matrix_adder_quarter
                     AND t.horizon = a.cm_matrix_adder_horizon
                     AND a.cm_matrix_adder_type = '{adderType}'
                     AND a.cm_matrix_adder_for = '{adderFor}'
                   WHERE t.sb = :sbName
                     AND t.horizon = :horizon{locClause}
                   GROUP BY CASE WHEN t.quarter IS NULL THEN t.fy ELSE t.fy || ' ' || t.quarter END
                   ORDER BY CASE WHEN t.quarter IS NULL THEN t.fy ELSE t.fy || ' ' || t.quarter END ASC";
    }

    // Test starts: demand (TS_DEMAND + Adder TS) and actual (TS_ACTUAL + Change TS).
    private Task<List<object>> QueryTsDemandAsync(string sbName, string horizon, string? loc)
        => RunSeriesAsync(_factory.Create, SeriesSql("t.ts_demand", "TS", "Adder", loc), sbName, horizon, loc);

    private Task<List<object>> QueryTsActualAsync(string sbName, string horizon, string? loc)
        => RunSeriesAsync(_factory.Create, SeriesSql("t.ts_actual", "TS", "Change", loc), sbName, horizon, loc);

    // Test effort (RTU): demand (RTU_PLAN + Adder RTU) and actual (RTU_ACT + Change RTU).
    private Task<List<object>> QueryRtuDemandAsync(string sbName, string horizon, string? loc)
        => RunSeriesAsync(_factory.Create, SeriesSql("t.rtu_plan", "RTU", "Adder", loc), sbName, horizon, loc);

    private Task<List<object>> QueryRtuActualAsync(string sbName, string horizon, string? loc)
        => RunSeriesAsync(_factory.Create, SeriesSql("t.rtu_act", "RTU", "Change", loc), sbName, horizon, loc);

    // Test cost: actual (COST_ACT + Change COST) over quarters.
    private Task<List<object>> QueryCostActualAsync(string sbName, string horizon, string? loc)
        => RunSeriesAsync(_factory.Create, SeriesSql("t.cost_act", "COST", "Change", loc), sbName, horizon, loc);

    // Cost demand: computed from the test-effort projection plus depreciation and the
    // COST adder, annualised (x4) for FY-only rows. The chart line uses the full
    // "demand with adder" total (rfcWoAdder + COST adder).
    private async Task<List<object>> QueryCostDemandAsync(string sbName, string horizon, string? loc)
    {
        var comps = await QueryCostDemandComponentsAsync(sbName, horizon, loc);
        return comps
            .Select(c => (object)new { label = c.Label, value = c.RfcWo + c.Depr + c.Addc })
            .ToList();
    }

    // Common LEFT JOIN from asb_ts_actual (alias t) to a single adder row of the given
    // type ('Adder'/'Change') and measure ('TS'/'RTU'/'COST'). The type/for values are
    // constant, code-controlled literals (not user input).
    private static string AdderJoin(string alias, string adderType, string adderFor) => $@"
                    LEFT JOIN rpt.cm_matrix_sb_adder {alias}
                      ON t.loc = {alias}.cm_matrix_adder_location
                     AND t.sb = {alias}.cm_matrix_adder_sb_name
                     AND t.fy || '-' || t.quarter = {alias}.cm_matrix_adder_fy || '-' || {alias}.cm_matrix_adder_quarter
                     AND t.horizon = {alias}.cm_matrix_adder_horizon
                     AND {alias}.cm_matrix_adder_type = '{adderType}'
                     AND {alias}.cm_matrix_adder_for = '{adderFor}'";

    // Cost-demand components per fiscal quarter:
    //   rtu_rfc = (SUM(ts_demand) + SUM(adder_ts)) * SUM("RTU/TS") * 3
    //             i.e. (TSpM RFC Demand w/o adder + TS adder) * RTU/TS * 3  ("RTU RFC demand")
    //   rfc_wo = rtu_rfc * SUM("COST/RTU") / 1000
    //            i.e. (RTU RFC demand * cost/rtu) / 1000  ("Cost RFC w/o Depreciation")
    //   depr   = SUM(depreciation)                            ("Cost RFC Depreciation")
    //   addc   = SUM(adder_cost)                              ("Adder Value Cost Demand")
    // All three are multiplied by 4 for FY-only rows (quarter IS NULL) to annualise.
    private static string CostDemandComponentsSql(string? loc)
    {
        var locClause = string.IsNullOrWhiteSpace(loc) ? "" : " AND t.loc = :loc";
        const string labelExpr = "CASE WHEN t.quarter IS NULL THEN t.fy ELSE t.fy || ' ' || t.quarter END";
        return $@"SELECT label,
                         CAST(CASE WHEN isq = 1 THEN rfc_wo ELSE rfc_wo * 4 END AS BINARY_DOUBLE) AS rfc_wo,
                         CAST(CASE WHEN isq = 1 THEN depr   ELSE depr   * 4 END AS BINARY_DOUBLE) AS depr,
                         CAST(CASE WHEN isq = 1 THEN addc   ELSE addc   * 4 END AS BINARY_DOUBLE) AS addc
                    FROM (
                      SELECT {labelExpr} AS label,
                             CASE WHEN t.quarter IS NULL THEN 0 ELSE 1 END AS isq,
                             ((SUM(TO_NUMBER(t.ts_demand DEFAULT 0 ON CONVERSION ERROR))
                                 + SUM(NVL(TO_NUMBER(ats.cm_matrix_adder_value DEFAULT 0 ON CONVERSION ERROR), 0)))
                               * SUM(TO_NUMBER(t.""RTU/TS"" DEFAULT 0 ON CONVERSION ERROR)) * 3)
                               * SUM(TO_NUMBER(t.""COST/RTU"" DEFAULT 0 ON CONVERSION ERROR)) / 1000 AS rfc_wo,
                             SUM(TO_NUMBER(t.depreciation DEFAULT 0 ON CONVERSION ERROR)) AS depr,
                             SUM(NVL(TO_NUMBER(ac.cm_matrix_adder_value DEFAULT 0 ON CONVERSION ERROR), 0)) AS addc
                        FROM rpt.asb_ts_actual t{AdderJoin("ats", "Adder", "TS")}{AdderJoin("ac", "Adder", "COST")}
                       WHERE t.sb = :sbName AND t.horizon = :horizon{locClause}
                       GROUP BY {labelExpr}, CASE WHEN t.quarter IS NULL THEN 0 ELSE 1 END
                    )
                   ORDER BY label ASC";
    }

    private async Task<List<CostDemandRow>> QueryCostDemandComponentsAsync(string sbName, string horizon, string? loc)
    {
        var rows = new List<CostDemandRow>();
        await using var conn = _factory.Create();
        await conn.OpenAsync();
        await using var cmd = new OracleCommand(CostDemandComponentsSql(loc), conn) { BindByName = true };
        cmd.Parameters.Add(new OracleParameter("sbName", sbName));
        cmd.Parameters.Add(new OracleParameter("horizon", horizon ?? (object)DBNull.Value));
        if (!string.IsNullOrWhiteSpace(loc))
        {
            cmd.Parameters.Add(new OracleParameter("loc", loc));
        }

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new CostDemandRow(
                reader["label"]?.ToString() ?? "",
                reader["rfc_wo"] == DBNull.Value ? 0d : Convert.ToDouble(reader["rfc_wo"]),
                reader["depr"] == DBNull.Value ? 0d : Convert.ToDouble(reader["depr"]),
                reader["addc"] == DBNull.Value ? 0d : Convert.ToDouble(reader["addc"])));
        }
        return rows;
    }

    // Per-location detailed table data: returns (tsRows, rtuRows, costRows). Each row
    // carries the raw component values; the frontend derives "with adder" totals,
    // utilization and deviation so all rounding lives in one place.
    private async Task<(List<object> Ts, List<object> Rtu, List<object> Cost)> QueryBreakdownAsync(
        string sbName, string horizon, string loc)
    {
        var baseRows = await QueryBaseSumsAsync(sbName, horizon, loc);
        var adders = await QueryAdderSumsAsync(sbName, horizon, loc);
        var costComps = (await QueryCostDemandComponentsAsync(sbName, horizon, loc))
            .ToDictionary(c => c.Label, c => c, StringComparer.OrdinalIgnoreCase);

        var ts = new List<object>();
        var rtu = new List<object>();
        var cost = new List<object>();

        foreach (var b in baseRows)
        {
            var a = adders.GetValueOrDefault(b.Label) ?? AdderRow.Empty;

            ts.Add(new
            {
                label = b.Label,
                baseDemand = b.TsDemand,
                adderDemand = a.AdderTs,
                baseActual = b.TsActual,
                changeActual = a.ChangeTs
            });

            rtu.Add(new
            {
                label = b.Label,
                baseDemand = b.RtuPlan,
                adderDemand = a.AdderRtu,
                baseActual = b.RtuAct,
                changeActual = a.ChangeRtu,
                rtuTs = b.RtuTs
            });

            costComps.TryGetValue(b.Label, out var cd);
            cost.Add(new
            {
                label = b.Label,
                rfcWoDemand = cd?.RfcWo ?? 0d,
                depreciation = cd?.Depr ?? 0d,
                adderDemand = cd?.Addc ?? 0d,
                baseActual = b.CostAct,
                changeActual = a.ChangeCost,
                costRtu = b.CostRtu
            });
        }

        return (ts, rtu, cost);
    }

    // One pass over asb_ts_actual summing every base measure per fiscal quarter.
    private async Task<List<BaseRow>> QueryBaseSumsAsync(string sbName, string horizon, string loc)
    {
        const string labelExpr = "CASE WHEN t.quarter IS NULL THEN t.fy ELSE t.fy || ' ' || t.quarter END";
        var sql = $@"SELECT {labelExpr} AS label,
                            SUM(TO_NUMBER(t.ts_demand DEFAULT 0 ON CONVERSION ERROR)) AS ts_demand,
                            SUM(TO_NUMBER(t.ts_actual DEFAULT 0 ON CONVERSION ERROR)) AS ts_actual,
                            SUM(TO_NUMBER(t.rtu_plan  DEFAULT 0 ON CONVERSION ERROR)) AS rtu_plan,
                            SUM(TO_NUMBER(t.rtu_act   DEFAULT 0 ON CONVERSION ERROR)) AS rtu_act,
                            SUM(TO_NUMBER(t.cost_act  DEFAULT 0 ON CONVERSION ERROR)) AS cost_act,
                            SUM(TO_NUMBER(t.depreciation DEFAULT 0 ON CONVERSION ERROR)) AS depr,
                            SUM(TO_NUMBER(t.""RTU/TS""  DEFAULT 0 ON CONVERSION ERROR)) AS rtu_ts,
                            SUM(TO_NUMBER(t.""COST/RTU"" DEFAULT 0 ON CONVERSION ERROR)) AS cost_rtu
                       FROM rpt.asb_ts_actual t
                      WHERE t.sb = :sbName AND t.horizon = :horizon AND t.loc = :loc
                      GROUP BY {labelExpr}
                      ORDER BY {labelExpr} ASC";

        var rows = new List<BaseRow>();
        await using var conn = _factory.Create();
        await conn.OpenAsync();
        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add(new OracleParameter("sbName", sbName));
        cmd.Parameters.Add(new OracleParameter("horizon", horizon ?? (object)DBNull.Value));
        cmd.Parameters.Add(new OracleParameter("loc", loc));

        await using var reader = await cmd.ExecuteReaderAsync();
        double D(string c) => reader[c] == DBNull.Value ? 0d : Convert.ToDouble(reader[c]);
        while (await reader.ReadAsync())
        {
            rows.Add(new BaseRow(
                reader["label"]?.ToString() ?? "",
                D("ts_demand"), D("ts_actual"), D("rtu_plan"), D("rtu_act"),
                D("cost_act"), D("depr"), D("rtu_ts"), D("cost_rtu")));
        }
        return rows;
    }

    // One pass over the adder table summing each adder/change value per fiscal quarter.
    private async Task<Dictionary<string, AdderRow>> QueryAdderSumsAsync(string sbName, string horizon, string loc)
    {
        const string labelExpr = "CASE WHEN a.cm_matrix_adder_quarter IS NULL THEN a.cm_matrix_adder_fy " +
                                 "ELSE a.cm_matrix_adder_fy || ' ' || a.cm_matrix_adder_quarter END";
        string Pick(string type, string measure) =>
            $"SUM(CASE WHEN a.cm_matrix_adder_type = '{type}' AND a.cm_matrix_adder_for = '{measure}' " +
            "THEN TO_NUMBER(a.cm_matrix_adder_value DEFAULT 0 ON CONVERSION ERROR) ELSE 0 END)";

        var sql = $@"SELECT {labelExpr} AS label,
                            {Pick("Adder", "TS")}    AS adder_ts,
                            {Pick("Change", "TS")}   AS change_ts,
                            {Pick("Adder", "RTU")}   AS adder_rtu,
                            {Pick("Change", "RTU")}  AS change_rtu,
                            {Pick("Adder", "COST")}  AS adder_cost,
                            {Pick("Change", "COST")} AS change_cost
                       FROM rpt.cm_matrix_sb_adder a
                      WHERE a.cm_matrix_adder_sb_name = :sbName
                        AND a.cm_matrix_adder_horizon = :horizon
                        AND a.cm_matrix_adder_location = :loc
                      GROUP BY {labelExpr}";

        var map = new Dictionary<string, AdderRow>(StringComparer.OrdinalIgnoreCase);
        await using var conn = _factory.Create();
        await conn.OpenAsync();
        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        cmd.Parameters.Add(new OracleParameter("sbName", sbName));
        cmd.Parameters.Add(new OracleParameter("horizon", horizon ?? (object)DBNull.Value));
        cmd.Parameters.Add(new OracleParameter("loc", loc));

        await using var reader = await cmd.ExecuteReaderAsync();
        double D(string c) => reader[c] == DBNull.Value ? 0d : Convert.ToDouble(reader[c]);
        while (await reader.ReadAsync())
        {
            map[reader["label"]?.ToString() ?? ""] = new AdderRow(
                D("adder_ts"), D("change_ts"), D("adder_rtu"),
                D("change_rtu"), D("adder_cost"), D("change_cost"));
        }
        return map;
    }

    private sealed record BaseRow(string Label, double TsDemand, double TsActual, double RtuPlan,
        double RtuAct, double CostAct, double Depr, double RtuTs, double CostRtu);

    private sealed record AdderRow(double AdderTs, double ChangeTs, double AdderRtu,
        double ChangeRtu, double AdderCost, double ChangeCost)
    {
        public static readonly AdderRow Empty = new(0, 0, 0, 0, 0, 0);
    }

    private sealed record CostDemandRow(string Label, double RfcWo, double Depr, double Addc);

    // Pareto by client corridor (descending volume).
    // The asb_ts_actual.cc column is largely empty, so we derive the corridor from the
    // SB's client mapping: sum RTU per location, then roll each location up to its corridor.
    private async Task<List<object>> QueryParetoAsync(string sbId, string sbName, string horizon, string? loc)
    {
        // 1. corridor <-> lab (cloc id) pairs for this SB.
        var pairs = await GetCorridorClocPairsAsync(sbId);
        if (pairs.Count == 0)
        {
            return new List<object>();
        }

        // 2. resolve cloc ids -> location names (REALIS), matching asb_ts_actual.loc.
        var clocNames = await ResolveClocNamesAsync(pairs.Select(p => p.Cloc).Distinct().ToList());

        // 3. location name -> corridor (first wins).
        var locToCorridor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (corridor, cloc) in pairs)
        {
            if (string.IsNullOrWhiteSpace(corridor)) continue;
            if (clocNames.TryGetValue(cloc, out var name) && !string.IsNullOrWhiteSpace(name) &&
                !locToCorridor.ContainsKey(name))
            {
                locToCorridor[name] = corridor;
            }
        }
        if (locToCorridor.Count == 0)
        {
            return new List<object>();
        }

        // 4. sum RTU per location from the actuals.
        var locClause = string.IsNullOrWhiteSpace(loc) ? "" : " AND t.loc = :loc";
        var sql = $@"SELECT t.loc AS label,
                            SUM(TO_NUMBER(t.rtu_act DEFAULT 0 ON CONVERSION ERROR)) AS value
                       FROM rpt.asb_ts_actual t
                      WHERE t.sb = :sbName
                        AND t.horizon = :horizon{locClause}
                        AND t.loc IS NOT NULL
                      GROUP BY t.loc";

        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        await using (var conn = _factory.Create())
        {
            await conn.OpenAsync();
            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add(new OracleParameter("sbName", sbName));
            cmd.Parameters.Add(new OracleParameter("horizon", horizon ?? (object)DBNull.Value));
            if (!string.IsNullOrWhiteSpace(loc))
            {
                cmd.Parameters.Add(new OracleParameter("loc", loc));
            }

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var locName = reader["label"]?.ToString() ?? "";
                var val = reader["value"] == DBNull.Value ? 0d : Convert.ToDouble(reader["value"]);
                if (locToCorridor.TryGetValue(locName, out var corridor))
                {
                    totals[corridor] = totals.GetValueOrDefault(corridor) + val;
                }
            }
        }

        return totals
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (object)new { label = kv.Key, value = kv.Value })
            .ToList();
    }

    // corridor + lab cloc id pairs for a SB, from the CM matrix client mapping.
    private async Task<List<(string Corridor, decimal Cloc)>> GetCorridorClocPairsAsync(string sbId)
    {
        var pairs = new List<(string, decimal)>();
        await using var conn = _factory.Create();
        await conn.OpenAsync();
        await using var cmd = new OracleCommand(
            @"SELECT DISTINCT c.cm_matrix_client_corridor        AS cc,
                              c.cm_matrix_client_lab_realis_cloc AS loc
                FROM cm_matrix_client c
                JOIN cm_matrix_sb sb ON sb.cm_matrix_sb_id = c.cm_matrix_client_sb_id
               WHERE sb.cm_matrix_sb_id = :sbId
                 AND sb.cm_matrix_sb_valid = 'Y'", conn)
            { BindByName = true };
        cmd.Parameters.Add(new OracleParameter("sbId", sbId));

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var cc = reader["cc"]?.ToString() ?? "";
            if (reader["loc"] != DBNull.Value)
            {
                pairs.Add((cc, Convert.ToDecimal(reader["loc"])));
            }
        }

        return pairs;
    }

    // Resolves REALIS cloc ids to their location names.
    private async Task<Dictionary<decimal, string>> ResolveClocNamesAsync(IReadOnlyList<decimal> clocIds)
    {
        var map = new Dictionary<decimal, string>();
        if (clocIds.Count == 0)
        {
            return map;
        }

        var binds = clocIds.Select((_, idx) => $":c{idx}").ToArray();
        var sql = $@"SELECT g.cloc_id, g.cloc_location_name
                       FROM ctlg_location_type g
                      WHERE g.cloc_id IN ({string.Join(", ", binds)})";

        await using var conn = _realisFactory.Create();
        await conn.OpenAsync();
        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
        for (var i = 0; i < clocIds.Count; i++)
        {
            cmd.Parameters.Add(new OracleParameter($"c{i}", clocIds[i]));
        }

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = Convert.ToDecimal(reader["cloc_id"]);
            map[id] = reader["cloc_location_name"]?.ToString() ?? "";
        }

        return map;
    }

    // Generic helper: runs a query that returns two columns (label, value) and
    // optionally binds :sbName, :horizon and :loc parameters when present in the SQL.
    private async Task<List<object>> RunSeriesAsync(
        Func<OracleConnection> factory,
        string sql,
        string sbName,
        string horizon,
        string? loc)
    {
        var points = new List<object>();

        await using var conn = factory();
        await conn.OpenAsync();
        await using var cmd = new OracleCommand(sql, conn) { BindByName = true };

        if (sql.Contains(":sbName", StringComparison.OrdinalIgnoreCase))
        {
            cmd.Parameters.Add(new OracleParameter("sbName", sbName));
        }
        if (sql.Contains(":horizon", StringComparison.OrdinalIgnoreCase))
        {
            cmd.Parameters.Add(new OracleParameter("horizon", horizon ?? (object)DBNull.Value));
        }
        if (sql.Contains(":loc", StringComparison.OrdinalIgnoreCase))
        {
            cmd.Parameters.Add(new OracleParameter("loc", string.IsNullOrWhiteSpace(loc) ? (object)DBNull.Value : loc));
        }

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            points.Add(new
            {
                label = reader["label"]?.ToString() ?? "",
                value = reader["value"] == DBNull.Value ? 0d : Convert.ToDouble(reader["value"])
            });
        }

        return points;
    }
}
