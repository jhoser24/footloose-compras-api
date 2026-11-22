
using Npgsql;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Registrar los controladores de los servicios SOA.
builder.Services.AddControllers();

// Habilitar la documentación OpenAPI.
builder.Services.AddOpenApi();

// Configurar la conexión compartida con Supabase.
builder.Services.AddSingleton<NpgsqlDataSource>(services =>
{
    var configuracion =
        services.GetRequiredService<IConfiguration>();

    var cadenaConexion =
        configuracion.GetConnectionString("FootlooseDb");

    if (string.IsNullOrWhiteSpace(cadenaConexion))
    {
        throw new InvalidOperationException(
            "No se encontro la conexion FootlooseDb.");
    }

    return NpgsqlDataSource.Create(cadenaConexion);
});

var app = builder.Build();

// Documentación de la API en desarrollo.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    app.MapScalarApiReference(options =>
    {
        options
            .WithTitle("Footloose Academico - Servicios SOA")
            .AddServer(
                "http://127.0.0.1:5290",
                "VS Code local"
            );
    });
}

// Habilitar las rutas de los controladores.
app.MapControllers();

app.Run();
