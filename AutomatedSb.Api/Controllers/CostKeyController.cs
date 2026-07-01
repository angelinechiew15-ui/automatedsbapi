using AutomatedSb.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Controllers;

/// <summary>
/// Provides the Cost Key overview data used by the Angular Cost Key page.
/// Queries rpt.asb_ts_actual directly (same source as LabSummaryController)
/// using TO_NUMBER(... DEFAULT 0 ON CONVERSION ERROR) on every varchar numeric
/// column to avoid the ORA-01722 thrown by the bare TO_NUMBER calls in v_sb_asb_data.
/// </summary>
[ApiController]
[Route("api/cost-key")]
public class CostKeyController : ControllerBase
{
    private readonly IOracleConnectionFactory _factory;
    private readonly IOracleRealisConnectionFactory _realisFactory;
    private readonly ILogger<CostKeyController> _logger;

    public CostKeyController(
        IOracleConnectionFactory factory,
        IOracleRealisConnectionFactory realisFactory,
        ILogger<CostKeyController> logger)
    {
        _factory = factory;
        _realisFactory = realisFactory;
        _logger = logger;
    }

    // GET api/cost-key/overview
    // Optional query params:
    //   horizon - overrides the default latest horizon from v_sb_asb_data
    //   fy      - filters the final rows by FY
    //   loc     - filters the final rows by normalized location
    //   sb      - filters the final rows by SB name
    [HttpGet("overview")]
    public async Task<ActionResult> GetOverview(
        [FromQuery] string? horizon,
        [FromQuery] string? fy,
        [FromQuery] string? loc,
        [FromQuery] string? sb)
    {
        try
        {
            var targetHorizon = string.IsNullOrWhiteSpace(horizon)
                ? await ResolveLatestHorizonAsync()
                : horizon.Trim();

            if (string.IsNullOrWhiteSpace(targetHorizon))
            {
                return Ok(Array.Empty<object>());
            }

            var horizonWindow = await ResolveHorizonWindowAsync(targetHorizon, 4);
            if (horizonWindow is null)
            {
                return Ok(Array.Empty<object>());
            }

            var horizonsToLoad = new[] { horizonWindow.CurrentHorizon }
                .Concat(horizonWindow.PastHorizons)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var mappingRows = await LoadMappingRowsAsync();
            var overviewByHorizon = new Dictionary<string, List<CostKeyOverviewRow>>(StringComparer.OrdinalIgnoreCase);

            foreach (var horizonValue in horizonsToLoad)
            {
                var rawRows = await LoadRawRowsAsync(horizonValue, fy, loc, sb);
                overviewByHorizon[horizonValue] = BuildOverview(rawRows, mappingRows);
            }

            if (!overviewByHorizon.TryGetValue(horizonWindow.CurrentHorizon, out var currentRows))
            {
                return Ok(Array.Empty<object>());
            }

            var result = MergeHistoricalCosts(currentRows, overviewByHorizon, horizonWindow.PastHorizons);
            return Ok(result);
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "GetOverview cost key failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    private async Task<string?> ResolveLatestHorizonAsync()
    {
        const string sql = @"
            SELECT MAX(horizon)
              FROM rpt.asb_ts_actual
             WHERE horizon IS NOT NULL";

        await using var conn = _factory.Create();
        await conn.OpenWithNlsAsync();
        await using var cmd = new OracleCommand(sql, conn);
        var scalar = await cmd.ExecuteScalarAsync();
        return scalar == null || scalar == DBNull.Value ? null : scalar.ToString();
    }

    private async Task<HorizonWindow?> ResolveHorizonWindowAsync(string horizon, int pastCount)
    {
        const string sql = @"
            SELECT rhz_id, rhz_name
              FROM rfc_horizon
             ORDER BY rhz_id DESC";

        await using var conn = _realisFactory.Create();
        await conn.OpenWithNlsAsync();
        await using var cmd = new OracleCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var horizons = new List<HorizonRef>();
        while (await reader.ReadAsync())
        {
            horizons.Add(new HorizonRef(
                reader["rhz_id"] == DBNull.Value ? -1 : Convert.ToInt32(reader["rhz_id"]),
                reader["rhz_name"]?.ToString() ?? string.Empty));
        }

        if (horizons.Count == 0)
        {
            return null;
        }

        var selectedIndex = -1;
        if (int.TryParse(horizon, out var horizonId))
        {
            selectedIndex = horizons.FindIndex(item => item.Id == horizonId);
        }

        if (selectedIndex < 0)
        {
            selectedIndex = horizons.FindIndex(item => string.Equals(item.Name, horizon, StringComparison.OrdinalIgnoreCase));
        }

        if (selectedIndex < 0)
        {
            return new HorizonWindow(horizon.Trim(), Array.Empty<string>());
        }

        var currentHorizon = horizons[selectedIndex].Name;
        var pastHorizons = horizons
            .Skip(selectedIndex + 1)
            .Take(pastCount)
            .Select(item => item.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        return new HorizonWindow(currentHorizon, pastHorizons);
    }

    private async Task<List<RawCostKeyRow>> LoadRawRowsAsync(string horizon, string? fy, string? loc, string? sb)
    {
        await using var conn = _factory.Create();
        await conn.OpenWithNlsAsync();

        await using var cmd = new OracleCommand(BuildSql(fy, loc, sb), conn) { BindByName = true };
        cmd.Parameters.Add(new OracleParameter("horizon", OracleDbType.Varchar2) { Value = horizon });
        if (!string.IsNullOrWhiteSpace(fy)) cmd.Parameters.Add(new OracleParameter("fy", OracleDbType.Varchar2) { Value = fy.Trim() });
        if (!string.IsNullOrWhiteSpace(loc)) cmd.Parameters.Add(new OracleParameter("loc", OracleDbType.Varchar2) { Value = loc.Trim() });
        if (!string.IsNullOrWhiteSpace(sb)) cmd.Parameters.Add(new OracleParameter("sb", OracleDbType.Varchar2) { Value = sb.Trim() });

        await using var reader = await cmd.ExecuteReaderAsync();
        var rawRows = new List<RawCostKeyRow>();

        static double Dbl(object raw) =>
            raw == null || raw == DBNull.Value ? 0d : Convert.ToDouble(raw);

        while (await reader.ReadAsync())
        {
            rawRows.Add(new RawCostKeyRow
            {
                Fy = reader["fy"]?.ToString() ?? string.Empty,
                Horizon = reader["horizon"]?.ToString() ?? string.Empty,
                FyQuarter = reader["fy_quarter"]?.ToString() ?? string.Empty,
                Loc = reader["loc"]?.ToString() ?? string.Empty,
                Sb = reader["sb"]?.ToString() ?? string.Empty,
                TsDemand = Dbl(reader["ts_demand"]),
                AdderTs = Dbl(reader["adder_ts"]),
                RtuTs = Dbl(reader["rtu_ts"]),
                AdderRtu = Dbl(reader["adder_rtu"]),
                CostRtu = Dbl(reader["cost_rtu"]),
                Depreciation = Dbl(reader["depreciation"]),
                AdderCost = Dbl(reader["adder_cost"]),
            });
        }

        return rawRows;
    }

    // Mirrors the LabSummaryController query pattern: reads rpt.asb_ts_actual directly
    // with TO_NUMBER(... DEFAULT 0 ON CONVERSION ERROR) on every varchar numeric column
    // so the German NLS locale (NLS_NUMERIC_CHARACTERS = ,.) never triggers ORA-01722.
    private static string BuildSql(string? fy, string? loc, string? sb)
    {
        static string NumExpr(string expr) =>
            $"TO_NUMBER({expr} DEFAULT 0 ON CONVERSION ERROR)";

        var extra = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(fy))  extra.Append(" AND t.fy  = :fy");
        if (!string.IsNullOrWhiteSpace(loc)) extra.Append(" AND t.loc = :loc");
        if (!string.IsNullOrWhiteSpace(sb))  extra.Append(" AND t.sb  = :sb");

        return $@"
            SELECT
                t.fy,
                t.horizon,
                CASE WHEN t.quarter IS NULL THEN t.fy ELSE t.fy || ' ' || t.quarter END AS fy_quarter,
                CASE
                    WHEN UPPER(TRIM(t.loc)) IN (TO_NCHAR('RPT MEL'), TO_NCHAR('TTM'), TO_NCHAR('KESM')) THEN TO_NCHAR('RPT MEL')
                    ELSE t.loc
                END AS loc,
                t.sb,
                CAST({NumExpr("t.ts_demand")} AS BINARY_DOUBLE) AS ts_demand,
                CAST(NVL({NumExpr("a_ts.cm_matrix_adder_value")}, 0) AS BINARY_DOUBLE) AS adder_ts,
                CAST({NumExpr(@"t.""RTU/TS""")} AS BINARY_DOUBLE) AS rtu_ts,
                CAST(NVL({NumExpr("a_rtu.cm_matrix_adder_value")}, 0) AS BINARY_DOUBLE) AS adder_rtu,
                CAST({NumExpr(@"t.""COST/RTU""")} AS BINARY_DOUBLE) AS cost_rtu,
                CAST({NumExpr("t.depreciation")} AS BINARY_DOUBLE) AS depreciation,
                CAST(NVL({NumExpr("a_cost.cm_matrix_adder_value")}, 0) AS BINARY_DOUBLE) AS adder_cost
            FROM rpt.asb_ts_actual t
            LEFT JOIN rpt.cm_matrix_sb_adder a_ts
              ON t.loc = a_ts.cm_matrix_adder_location
             AND t.sb  = a_ts.cm_matrix_adder_sb_name
             AND t.fy || '-' || t.quarter = a_ts.cm_matrix_adder_fy || '-' || a_ts.cm_matrix_adder_quarter
             AND t.horizon = a_ts.cm_matrix_adder_horizon
             AND a_ts.cm_matrix_adder_type = 'Adder'
             AND a_ts.cm_matrix_adder_for  = 'TS'
            LEFT JOIN rpt.cm_matrix_sb_adder a_rtu
              ON t.loc = a_rtu.cm_matrix_adder_location
             AND t.sb  = a_rtu.cm_matrix_adder_sb_name
             AND t.fy || '-' || t.quarter = a_rtu.cm_matrix_adder_fy || '-' || a_rtu.cm_matrix_adder_quarter
             AND t.horizon = a_rtu.cm_matrix_adder_horizon
             AND a_rtu.cm_matrix_adder_type = 'Adder'
             AND a_rtu.cm_matrix_adder_for  = 'RTU'
            LEFT JOIN rpt.cm_matrix_sb_adder a_cost
              ON t.loc = a_cost.cm_matrix_adder_location
             AND t.sb  = a_cost.cm_matrix_adder_sb_name
             AND t.fy || '-' || t.quarter = a_cost.cm_matrix_adder_fy || '-' || a_cost.cm_matrix_adder_quarter
             AND t.horizon = a_cost.cm_matrix_adder_horizon
             AND a_cost.cm_matrix_adder_type = 'Adder'
             AND a_cost.cm_matrix_adder_for  = 'COST'
            WHERE t.horizon = :horizon
              AND t.fy LIKE '%' || SUBSTR(t.horizon, 1, 2)
              AND t.loc IS NOT NULL
              AND t.sb  IS NOT NULL{extra}
            ORDER BY t.fy, loc, t.sb, fy_quarter";
    }

    private async Task<Dictionary<(string Sb, string Loc), List<MappingRow>>> LoadMappingRowsAsync()
    {
        const string sql = @"
            SELECT
                cm_matrix_cost_mapping_sb_affect AS sb_affect,
                cm_matrix_cost_mapping_lab AS lab,
                cm_matrix_cost_mapping_cc_percent AS cc_percent,
                cm_matrix_cost_mapping_receiver_wbs AS receiver_wbs,
                cm_matrix_cost_mapping_cc AS cc
            FROM cm_matrix_sb_cost_mapping";

        await using var conn = _factory.Create();
        await conn.OpenWithNlsAsync();
        await using var cmd = new OracleCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var mappings = new Dictionary<(string Sb, string Loc), List<MappingRow>>();

        while (await reader.ReadAsync())
        {
            var sb = reader["sb_affect"]?.ToString() ?? string.Empty;
            var lab = reader["lab"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sb) || string.IsNullOrWhiteSpace(lab))
            {
                continue;
            }

            var key = (Normalize(sb), Normalize(lab));
            if (!mappings.TryGetValue(key, out var list))
            {
                list = new List<MappingRow>();
                mappings[key] = list;
            }

            list.Add(new MappingRow
            {
                SbAffect = sb,
                Loc = lab,
                CcPercent = reader["cc_percent"] is DBNull or null ? (double?)null : Convert.ToDouble(reader["cc_percent"]),
                ReceiverWbs = reader["receiver_wbs"]?.ToString(),
                Cc = reader["cc"]?.ToString(),
            });
        }

        return mappings;
    }

    private static List<CostKeyOverviewRow> BuildOverview(
        IReadOnlyList<RawCostKeyRow> rows,
        IReadOnlyDictionary<(string Sb, string Loc), List<MappingRow>> mappings)
    {
        var aggregates = new Dictionary<(string Fy, string Loc, string Sb), CostKeyAggregate>();

        foreach (var row in rows)
        {
            var key = (row.Fy, row.Loc, row.Sb);
            mappings.TryGetValue((Normalize(row.Sb), Normalize(row.Loc)), out var mappingRows);
            var firstMapping = mappingRows?.FirstOrDefault();

            if (!aggregates.TryGetValue(key, out var aggregate))
            {
                aggregate = new CostKeyAggregate
                {
                    Fy = row.Fy,
                    Loc = row.Loc,
                    Sb = row.Sb,
                    SbAffect = firstMapping?.SbAffect,
                    CcPercent = firstMapping?.CcPercent,
                    ReceiverWbs = firstMapping?.ReceiverWbs,
                    Cc = firstMapping?.Cc,
                };
                aggregates[key] = aggregate;
            }

            aggregate.SbAffect ??= firstMapping?.SbAffect;
            aggregate.CcPercent ??= firstMapping?.CcPercent;
            aggregate.ReceiverWbs ??= firstMapping?.ReceiverWbs;
            aggregate.Cc ??= firstMapping?.Cc;

            var isQuarter = row.FyQuarter.Contains('Q', StringComparison.OrdinalIgnoreCase);
            var quarterBase = (row.TsDemand + row.AdderTs) * 3 * row.RtuTs + row.AdderRtu;
            var costRfcWoDepr = isQuarter
                ? quarterBase * row.CostRtu / 1000
                : quarterBase * row.CostRtu / 1000 * 4;
            var costDemand = isQuarter
                ? costRfcWoDepr + row.Depreciation + row.AdderCost
                : (costRfcWoDepr + row.Depreciation + row.AdderCost) * 4;

            aggregate.CostRfcWoDeprBefore += costRfcWoDepr;
            aggregate.CostDemandBefore += costDemand;
        }

        var splitRows = new List<SplitCostRow>();
        foreach (var aggregate in aggregates.Values)
        {
            mappings.TryGetValue((Normalize(aggregate.Sb), Normalize(aggregate.Loc)), out var mappingRows);
            var effectiveMappings = mappingRows?
                .Where(mapping =>
                    !string.IsNullOrWhiteSpace(mapping.ReceiverWbs)
                    && mapping.CcPercent.HasValue)
                .ToList();

            if (effectiveMappings == null || effectiveMappings.Count == 0)
            {
                continue;
            }

            foreach (var mapping in effectiveMappings)
            {
                var multiplier = ResolveCcMultiplier(mapping.CcPercent);
                splitRows.Add(new SplitCostRow
                {
                    Fy = aggregate.Fy,
                    Loc = aggregate.Loc,
                    ServiceBundle = mapping.SbAffect ?? aggregate.Sb,
                    ClientCorridor = mapping.Cc ?? string.Empty,
                    WbsElement = mapping.ReceiverWbs ?? string.Empty,
                    CcPercent = multiplier,
                    CostKeur = aggregate.CostDemandBefore * multiplier,
                });
            }
        }

        var totalsByPartition = splitRows
            .GroupBy(row => (row.Fy, row.Loc))
            .ToDictionary(
                group => group.Key,
                group => group.Sum(row => row.CostKeur));

        return splitRows
            .OrderBy(row => row.Fy)
            .ThenBy(row => row.Loc)
            .ThenBy(row => row.ServiceBundle)
            .ThenBy(row => row.ClientCorridor)
            .Select(row =>
            {
                totalsByPartition.TryGetValue((row.Fy, row.Loc), out var totalCostDemand);
                var key = totalCostDemand > 0 ? Math.Round(row.CostKeur / totalCostDemand, 4) : (double?)null;

                return new CostKeyOverviewRow(
                    row.Fy,
                    row.Loc,
                    row.ServiceBundle,
                    row.ClientCorridor,
                    row.WbsElement,
                    Math.Round(row.CcPercent, 4),
                    Math.Round(row.CostKeur, 2),
                    key,
                    Math.Round(totalCostDemand, 2),
                    new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase));
            })
            .ToList();
    }

    private static List<CostKeyOverviewRow> MergeHistoricalCosts(
        IReadOnlyList<CostKeyOverviewRow> currentRows,
        IReadOnlyDictionary<string, List<CostKeyOverviewRow>> overviewByHorizon,
        IReadOnlyList<string> pastHorizons)
    {
        var currentByKey = currentRows.ToDictionary(
            row => (row.Fy, row.Loc, row.ServiceBundle, row.ClientCorridor, row.WbsElement),
            row => row);

        foreach (var pastHorizon in pastHorizons)
        {
            if (!overviewByHorizon.TryGetValue(pastHorizon, out var pastRows))
            {
                continue;
            }

            var pastByKey = pastRows.ToDictionary(
                row => (row.Fy, row.Loc, row.ServiceBundle, row.ClientCorridor, row.WbsElement),
                row => row);

            foreach (var currentRow in currentByKey.Values)
            {
                if (pastByKey.TryGetValue((currentRow.Fy, currentRow.Loc, currentRow.ServiceBundle, currentRow.ClientCorridor, currentRow.WbsElement), out var pastRow))
                {
                    currentRow.HistoricalCosts[pastHorizon] = pastRow.CostKeur;
                }
                else
                {
                    currentRow.HistoricalCosts[pastHorizon] = null;
                }
            }
        }

        return currentByKey.Values
            .OrderBy(row => row.Fy)
            .ThenBy(row => row.Loc)
            .ThenBy(row => row.ServiceBundle)
            .ThenBy(row => row.ClientCorridor)
            .ThenBy(row => row.WbsElement)
            .ToList();
    }

    private static double ResolveCcMultiplier(double? ccPercent)
    {
        if (ccPercent is null)
        {
            return 1d;
        }

        var value = ccPercent.Value;
        if (value <= 0)
        {
            return 0d;
        }

        return value > 1d ? value / 100d : value;
    }


    private static string Normalize(string? value) => value?.Trim().ToUpperInvariant() ?? string.Empty;

    private sealed class RawCostKeyRow
    {
        public string Fy { get; init; } = string.Empty;
        public string Horizon { get; init; } = string.Empty;
        public string FyQuarter { get; init; } = string.Empty;
        public string Loc { get; init; } = string.Empty;
        public string Sb { get; init; } = string.Empty;
        public double TsDemand { get; init; }
        public double AdderTs { get; init; }
        public double RtuTs { get; init; }
        public double AdderRtu { get; init; }
        public double CostRtu { get; init; }
        public double Depreciation { get; init; }
        public double AdderCost { get; init; }
    }

    private sealed class CostKeyAggregate
    {
        public string Fy { get; init; } = string.Empty;
        public string Loc { get; init; } = string.Empty;
        public string Sb { get; init; } = string.Empty;
        public string? SbAffect { get; set; }
        public double? CcPercent { get; set; }
        public string? ReceiverWbs { get; set; }
        public string? Cc { get; set; }
        public double CostRfcWoDeprBefore { get; set; }
        public double CostDemandBefore { get; set; }
    }

    private sealed class MappingRow
    {
        public string? SbAffect { get; init; }
        public string? Loc { get; init; }
        public double? CcPercent { get; init; }
        public string? ReceiverWbs { get; init; }
        public string? Cc { get; init; }
    }

    private sealed class SplitCostRow
    {
        public string Fy { get; init; } = string.Empty;
        public string Loc { get; init; } = string.Empty;
        public string ServiceBundle { get; init; } = string.Empty;
        public string ClientCorridor { get; init; } = string.Empty;
        public string WbsElement { get; init; } = string.Empty;
        public double CcPercent { get; init; }
        public double CostKeur { get; init; }
    }

    private sealed record CostKeyOverviewRow(
        string Fy,
        string Loc,
        string ServiceBundle,
        string ClientCorridor,
        string WbsElement,
        double CcPercent,
        double CostKeur,
        double? Key,
        double TotalCostDemand,
        Dictionary<string, double?> HistoricalCosts);

    private sealed record HorizonWindow(string CurrentHorizon, IReadOnlyList<string> PastHorizons);

    private sealed record HorizonRef(int Id, string Name);
}
