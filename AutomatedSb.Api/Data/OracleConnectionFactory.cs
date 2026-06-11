using Microsoft.Extensions.Configuration;
using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Data;

public interface IOracleConnectionFactory
{
    OracleConnection Create();
}

public class OracleConnectionFactory : IOracleConnectionFactory
{
    private readonly string _connectionString;

    public OracleConnectionFactory(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("Oracle")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Oracle is not configured in appsettings.json.");
    }

    public OracleConnection Create() => new(_connectionString);
}
