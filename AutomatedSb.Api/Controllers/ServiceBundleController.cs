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
                    pareto = empty
                });
            }

            var tsDemand = await QueryTsDemandAsync(sbName, horizon, loc);
            var tsActual = await QueryTsActualAsync(sbName, horizon, loc);
            var rtuDemand = await QueryRtuDemandAsync(sbName, horizon, loc);
            var rtuActual = await QueryRtuActualAsync(sbName, horizon, loc);
            var costDemand = await QueryCostDemandAsync(sbName, horizon, loc);
            var costActual = await QueryCostActualAsync(sbName, horizon, loc);
            var pareto = await QueryParetoAsync(sbId, sbName, horizon, loc);

            return Ok(new
            {
                success = true,
                tsDemand,
                tsActual,
                rtuDemand,
                rtuActual,
                costDemand,
                costActual,
                pareto
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

    // Builds a time-series SQL over the base table for the given measure, grouped by fiscal quarter.
    // The v_sb_asb_data view casts dirty text columns with a plain TO_NUMBER, which throws ORA-01722
    // when aggregated. Querying the base table lets us use a safe conversion (DEFAULT 0 ON CONVERSION ERROR).
    private static string SeriesSql(string measure, string? loc)
    {
        var locClause = string.IsNullOrWhiteSpace(loc) ? "" : " AND t.loc = :loc";
        return $@"SELECT CASE WHEN t.quarter IS NULL THEN t.fy ELSE t.fy || ' ' || t.quarter END AS label,
                         SUM(TO_NUMBER({measure} DEFAULT 0 ON CONVERSION ERROR)) AS value
                    FROM rpt.asb_ts_actual t
                   WHERE t.sb = :sbName
                     AND t.horizon = :horizon{locClause}
                   GROUP BY CASE WHEN t.quarter IS NULL THEN t.fy ELSE t.fy || ' ' || t.quarter END
                   ORDER BY CASE WHEN t.quarter IS NULL THEN t.fy ELSE t.fy || ' ' || t.quarter END ASC";
    }

    // Test starts: demand (TS_DEMAND) and actual (TS_ACTUAL) over quarters.
    private Task<List<object>> QueryTsDemandAsync(string sbName, string horizon, string? loc)
        => RunSeriesAsync(_factory.Create, SeriesSql("t.ts_demand", loc), sbName, horizon, loc);

    private Task<List<object>> QueryTsActualAsync(string sbName, string horizon, string? loc)
        => RunSeriesAsync(_factory.Create, SeriesSql("t.ts_actual", loc), sbName, horizon, loc);

    // Test effort (RTU): demand (RTU_PLAN) and actual (RTU_ACT) over quarters.
    private Task<List<object>> QueryRtuDemandAsync(string sbName, string horizon, string? loc)
        => RunSeriesAsync(_factory.Create, SeriesSql("t.rtu_plan", loc), sbName, horizon, loc);

    private Task<List<object>> QueryRtuActualAsync(string sbName, string horizon, string? loc)
        => RunSeriesAsync(_factory.Create, SeriesSql("t.rtu_act", loc), sbName, horizon, loc);

    // Test cost: actual (COST_ACT) over quarters.
    private Task<List<object>> QueryCostActualAsync(string sbName, string horizon, string? loc)
        => RunSeriesAsync(_factory.Create, SeriesSql("t.cost_act", loc), sbName, horizon, loc);

    // Cost demand: no source column yet (formula to be provided). Returns a zero
    // placeholder aligned to the same quarters so the chart axis stays consistent.
    private Task<List<object>> QueryCostDemandAsync(string sbName, string horizon, string? loc)
        => RunSeriesAsync(_factory.Create, SeriesSql("0", loc), sbName, horizon, loc);

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
