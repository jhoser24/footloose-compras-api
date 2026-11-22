
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace Footloose.Compras.API.Controllers;

[ApiController]
[Route("api/v1/tiendas")]
public class TiendasController : ControllerBase
{
    private readonly NpgsqlDataSource _db;

    public TiendasController(NpgsqlDataSource db)
    {
        _db = db;
    }

    // Servicio 33: Consultar ubicación y horarios de tiendas
    [HttpGet]
    public async Task<ActionResult<List<TiendaDto>>> ConsultarTiendas(
        [FromQuery] string? textoBusqueda,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                t.id_tienda,
                t.codigo_tienda,
                t.nombre,
                t.direccion,
                t.telefono,
                t.latitud,
                t.longitud,
                t.permite_retiro_tienda,
                t.activa,
                h.id_horario,
                h.dia_semana,
                h.hora_apertura,
                h.hora_cierre,
                h.cerrado
            FROM public.tiendas AS t
            LEFT JOIN public.horarios_tienda AS h
                ON t.id_tienda = h.tienda_id
            WHERE t.activa = TRUE
              AND (
                  @busqueda IS NULL
                  OR t.nombre ILIKE '%' || @busqueda || '%'
                  OR t.codigo_tienda ILIKE '%' || @busqueda || '%'
                  OR t.direccion ILIKE '%' || @busqueda || '%'
              )
            ORDER BY
                t.nombre,
                CASE h.dia_semana
                    WHEN 'LUNES' THEN 1
                    WHEN 'MARTES' THEN 2
                    WHEN 'MIERCOLES' THEN 3
                    WHEN 'JUEVES' THEN 4
                    WHEN 'VIERNES' THEN 5
                    WHEN 'SABADO' THEN 6
                    WHEN 'DOMINGO' THEN 7
                    ELSE 8
                END;
            """;

        var tiendas = new Dictionary<Guid, TiendaDto>();

        await using var conexion =
            await _db.OpenConnectionAsync(cancellationToken);

        await using var comando =
            new NpgsqlCommand(sql, conexion);

        comando.Parameters.Add(
            "busqueda", NpgsqlDbType.Text
        ).Value = string.IsNullOrWhiteSpace(textoBusqueda)
            ? DBNull.Value
            : textoBusqueda.Trim();

        await using var lector =
            await comando.ExecuteReaderAsync(cancellationToken);

        while (await lector.ReadAsync(cancellationToken))
        {
            var idTienda = lector.GetGuid(0);

            if (!tiendas.TryGetValue(idTienda, out var tienda))
            {
                tienda = new TiendaDto
                {
                    IdTienda = idTienda,
                    CodigoTienda = lector.GetString(1),
                    Nombre = lector.GetString(2),
                    Direccion = lector.GetString(3),
                    Telefono = lector.IsDBNull(4)
                        ? null : lector.GetString(4),
                    Latitud = lector.IsDBNull(5)
                        ? null : lector.GetDecimal(5),
                    Longitud = lector.IsDBNull(6)
                        ? null : lector.GetDecimal(6),
                    PermiteRetiroTienda = lector.GetBoolean(7),
                    Activa = lector.GetBoolean(8)
                };

                tiendas.Add(idTienda, tienda);
            }

            if (!lector.IsDBNull(9))
            {
                tienda.Horarios.Add(new HorarioDto
                {
                    IdHorario = lector.GetGuid(9),
                    DiaSemana = lector.GetString(10),
                    HoraApertura = lector
                        .GetFieldValue<TimeOnly>(11)
                        .ToString("HH:mm"),
                    HoraCierre = lector
                        .GetFieldValue<TimeOnly>(12)
                        .ToString("HH:mm"),
                    Cerrado = lector.GetBoolean(13)
                });
            }
        }

        return Ok(tiendas.Values.ToList());
    }
}

// Datos de respuesta para las tiendas
public class TiendaDto
{
    public Guid IdTienda { get; set; }
    public string CodigoTienda { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Direccion { get; set; } = "";
    public string? Telefono { get; set; }
    public decimal? Latitud { get; set; }
    public decimal? Longitud { get; set; }
    public bool PermiteRetiroTienda { get; set; }
    public bool Activa { get; set; }
    public List<HorarioDto> Horarios { get; set; } = new();
}

// Datos de respuesta para los horarios
public class HorarioDto
{
    public Guid IdHorario { get; set; }
    public string DiaSemana { get; set; } = "";
    public string HoraApertura { get; set; } = "";
    public string HoraCierre { get; set; } = "";
    public bool Cerrado { get; set; }
}
