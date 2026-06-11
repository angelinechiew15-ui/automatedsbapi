using Microsoft.Extensions.Configuration;
using Oracle.ManagedDataAccess.Client;

namespace AutomatedSb.Api.Data
{
    public interface IOracleRealisConnectionFactory
    {
        OracleConnection Create();
    }

    public class OracleRealisConnectionFactory : IOracleRealisConnectionFactory
    {
        private readonly string _connectionString;

        public OracleRealisConnectionFactory(IConfiguration configuration)
        {
            // Assumes your appsettings.json has a section like "ConnectionStrings:RealisDb"
            _connectionString = configuration.GetConnectionString("OracleREALIS");
        }

        public OracleConnection Create()
        {
            return new OracleConnection(_connectionString);
        }
    }
}
