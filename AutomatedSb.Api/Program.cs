using AutomatedSb.Api.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Oracle.ManagedDataAccess.Client;

var builder = WebApplication.CreateBuilder(args);

const string CorsPolicy = "AllowAngular";

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicy, policy =>
    {
        var origins = builder.Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? new[] { "http://localhost:4200" };

        policy.WithOrigins(origins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register Oracle factory for the default database
builder.Services.AddScoped<IOracleConnectionFactory>(sp =>
    new OracleConnectionFactory(builder.Configuration));

// Register Oracle factory for the "Realis" database
builder.Services.AddScoped<IOracleRealisConnectionFactory>(sp =>
    new OracleRealisConnectionFactory(builder.Configuration));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(CorsPolicy);
app.UseAuthorization();
app.MapControllers();

app.Run();
