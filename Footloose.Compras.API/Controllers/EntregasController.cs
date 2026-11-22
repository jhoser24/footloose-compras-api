
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

[ApiController]
[Route("api/v1/entregas")]
public class EntregasController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;

    public EntregasController(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    [HttpGet("plazos")]
    public async Task<ActionResult<List<PlazoEntregaDto>>>
        ConsultarPlazosEntrega(
            [FromQuery] string? departamento,
            [FromQuery] string? provincia,
            [FromQuery] string? distrito,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                d.nombre AS departamento,
                p.nombre AS provincia,
                u.nombre AS distrito,
                pe.modalidad_entrega,
                pe.dias_minimos,
                pe.dias_maximos,
                pe.costo_envio,
                pe.envio_gratis_desde
            FROM public.plazos_entrega AS pe
            INNER JOIN public.ubicaciones_geograficas AS u
                ON u.id_ubicacion = pe.ubicacion_id
            INNER JOIN public.ubicaciones_geograficas AS p
                ON p.id_ubicacion = u.padre_id
            INNER JOIN public.ubicaciones_geograficas AS d
                ON d.id_ubicacion = p.padre_id
            WHERE pe.activo = TRUE
              AND u.activo = TRUE
              AND p.activo = TRUE
              AND d.activo = TRUE
              AND u.tipo_nivel = 'DISTRITO'
              AND p.tipo_nivel = 'PROVINCIA'
              AND d.tipo_nivel = 'DEPARTAMENTO'
              AND (
                  @departamento IS NULL
                  OR d.nombre ILIKE '%' || @departamento || '%'
              )
              AND (
                  @provincia IS NULL
                  OR p.nombre ILIKE '%' || @provincia || '%'
              )
              AND (
                  @distrito IS NULL
                  OR u.nombre ILIKE '%' || @distrito || '%'
              )
            ORDER BY
                d.nombre,
                p.nombre,
                u.nombre,
                pe.modalidad_entrega;
            """;

        var resultados = new List<PlazoEntregaDto>();

        await using var conexion =
            await _dataSource.OpenConnectionAsync(
                cancellationToken);

        await using var comando =
            new NpgsqlCommand(sql, conexion);

        comando.Parameters.Add(
            "departamento", NpgsqlDbType.Text
        ).Value = ValorParametro(departamento);

        comando.Parameters.Add(
            "provincia", NpgsqlDbType.Text
        ).Value = ValorParametro(provincia);

        comando.Parameters.Add(
            "distrito", NpgsqlDbType.Text
        ).Value = ValorParametro(distrito);

        await using var lector =
            await comando.ExecuteReaderAsync(
                cancellationToken);

        while (await lector.ReadAsync(cancellationToken))
        {
            resultados.Add(new PlazoEntregaDto
            {
                Departamento = lector.GetString(0),
                Provincia = lector.GetString(1),
                Distrito = lector.GetString(2),
                ModalidadEntrega = lector.GetString(3),
                DiasMinimos = lector.GetInt32(4),
                DiasMaximos = lector.GetInt32(5),
                CostoEnvio = lector.GetDecimal(6),
                EnvioGratisDesde = lector.IsDBNull(7)
                    ? null
                    : lector.GetDecimal(7)
            });
        }

        return Ok(resultados);
    }

    // Servicio 34: Consultar opciones de entrega de la compra
    [HttpGet("opciones")]
    public async Task<ActionResult<OpcionesEntregaDto>>
        ConsultarOpcionesEntrega(
            [FromQuery] Guid carritoId,
            [FromQuery] string? modalidadEntrega,
            [FromQuery] string? departamento,
            [FromQuery] string? provincia,
            [FromQuery] string? distrito,
            [FromQuery] string? textoBusquedaTienda,
            CancellationToken cancellationToken)
    {
        if (carritoId == Guid.Empty)
            return BadRequest("Debe indicar carritoId.");

        var modalidad = modalidadEntrega?
            .Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(modalidad))
            modalidad = null;

        if (modalidad != null &&
            modalidad != "ENVIO_DOMICILIO" &&
            modalidad != "RETIRO_TIENDA")
        {
            return BadRequest(
                "Use ENVIO_DOMICILIO o RETIRO_TIENDA.");
        }

        var resultado = new OpcionesEntregaDto
        {
            CarritoId = carritoId
        };

        await using var conexion =
            await _dataSource.OpenConnectionAsync(
                cancellationToken);

        // 1. Consultar el carrito actual
        const string sqlCarrito = """
            SELECT
                c.subtotal,
                c.descuento_total,
                c.total,
                COUNT(i.id_item)::integer,
                COALESCE(SUM(i.cantidad), 0)::integer
            FROM public.carritos c
            LEFT JOIN public.items_carrito i
                ON i.carrito_id = c.id_carrito
            WHERE c.id_carrito = @carritoId
              AND c.estado = 'ACTIVO'
              AND c.responsable_modulo = 'Jorge'
            GROUP BY
                c.id_carrito,
                c.subtotal,
                c.descuento_total,
                c.total;
            """;

        await using (var comando =
            new NpgsqlCommand(sqlCarrito, conexion))
        {
            comando.Parameters.Add(
                "carritoId", NpgsqlDbType.Uuid
            ).Value = carritoId;

            await using var lector =
                await comando.ExecuteReaderAsync(
                    cancellationToken);

            if (!await lector.ReadAsync(cancellationToken))
                return NotFound(
                    "No se encontro un carrito activo.");

            resultado.Subtotal = lector.GetDecimal(0);
            resultado.DescuentoTotal = lector.GetDecimal(1);
            resultado.TotalCarrito = lector.GetDecimal(2);
            resultado.CantidadLineas = lector.GetInt32(3);
            resultado.CantidadUnidades = lector.GetInt32(4);
        }

        if (resultado.CantidadLineas == 0)
            return BadRequest(
                "El carrito no contiene articulos.");

        // 2. Opciones de envio a domicilio
        if (modalidad != "RETIRO_TIENDA")
        {
            const string sqlDomicilio = """
                SELECT
                    d.nombre,
                    p.nombre,
                    u.nombre,
                    pe.modalidad_entrega,
                    pe.dias_minimos,
                    pe.dias_maximos,
                    pe.costo_envio,
                    pe.envio_gratis_desde
                FROM public.plazos_entrega pe
                INNER JOIN public.ubicaciones_geograficas u
                    ON u.id_ubicacion = pe.ubicacion_id
                INNER JOIN public.ubicaciones_geograficas p
                    ON p.id_ubicacion = u.padre_id
                INNER JOIN public.ubicaciones_geograficas d
                    ON d.id_ubicacion = p.padre_id
                WHERE pe.activo = TRUE
                  AND u.activo = TRUE
                  AND p.activo = TRUE
                  AND d.activo = TRUE
                  AND pe.modalidad_entrega IN (
                      'DELIVERY_REGULAR',
                      'DELIVERY_EXPRESS'
                  )
                  AND (
                      @departamento IS NULL
                      OR d.nombre ILIKE
                         '%' || @departamento || '%'
                  )
                  AND (
                      @provincia IS NULL
                      OR p.nombre ILIKE
                         '%' || @provincia || '%'
                  )
                  AND (
                      @distrito IS NULL
                      OR u.nombre ILIKE
                         '%' || @distrito || '%'
                  )
                ORDER BY
                    d.nombre, p.nombre,
                    u.nombre, pe.modalidad_entrega;
                """;

            await using var comando =
                new NpgsqlCommand(sqlDomicilio, conexion);

            comando.Parameters.Add(
                "departamento", NpgsqlDbType.Text
            ).Value = ValorParametro(departamento);

            comando.Parameters.Add(
                "provincia", NpgsqlDbType.Text
            ).Value = ValorParametro(provincia);

            comando.Parameters.Add(
                "distrito", NpgsqlDbType.Text
            ).Value = ValorParametro(distrito);

            await using var lector =
                await comando.ExecuteReaderAsync(
                    cancellationToken);

            while (await lector.ReadAsync(cancellationToken))
            {
                decimal costoBase = lector.GetDecimal(6);

                decimal? gratisDesde = lector.IsDBNull(7)
                    ? null
                    : lector.GetDecimal(7);

                bool aplicaGratis =
                    gratisDesde.HasValue &&
                    resultado.TotalCarrito >= gratisDesde.Value;

                resultado.EnvioDomicilio.Add(
                    new OpcionDomicilioDto
                    {
                        Departamento = lector.GetString(0),
                        Provincia = lector.GetString(1),
                        Distrito = lector.GetString(2),
                        Modalidad = lector.GetString(3),
                        DiasMinimos = lector.GetInt32(4),
                        DiasMaximos = lector.GetInt32(5),
                        CostoBase = costoBase,
                        EnvioGratisDesde = gratisDesde,
                        AplicaEnvioGratis = aplicaGratis,
                        CostoAplicable =
                            aplicaGratis ? 0 : costoBase
                    });
            }
        }

        // 3. Opciones de recojo en tienda
        if (modalidad != "ENVIO_DOMICILIO")
        {
            const string sqlTiendas = """
                SELECT
                    t.id_tienda,
                    t.codigo_tienda,
                    t.nombre,
                    t.direccion,
                    t.telefono,
                    h.dia_semana,
                    to_char(h.hora_apertura, 'HH24:MI'),
                    to_char(h.hora_cierre, 'HH24:MI'),
                    h.cerrado
                FROM public.tiendas t
                LEFT JOIN public.horarios_tienda h
                    ON h.tienda_id = t.id_tienda
                WHERE t.activa = TRUE
                  AND t.permite_retiro_tienda = TRUE
                  AND (
                      @busqueda IS NULL
                      OR t.nombre ILIKE
                         '%' || @busqueda || '%'
                      OR t.direccion ILIKE
                         '%' || @busqueda || '%'
                      OR t.codigo_tienda ILIKE
                         '%' || @busqueda || '%'
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

            await using var comando =
                new NpgsqlCommand(sqlTiendas, conexion);

            comando.Parameters.Add(
                "busqueda", NpgsqlDbType.Text
            ).Value = ValorParametro(textoBusquedaTienda);

            var tiendas = new Dictionary<Guid, TiendaRecojoDto>();

            await using var lector =
                await comando.ExecuteReaderAsync(
                    cancellationToken);

            while (await lector.ReadAsync(cancellationToken))
            {
                Guid idTienda = lector.GetGuid(0);

                if (!tiendas.TryGetValue(idTienda, out var tienda))
                {
                    tienda = new TiendaRecojoDto
                    {
                        IdTienda = idTienda,
                        Codigo = lector.GetString(1),
                        Nombre = lector.GetString(2),
                        Direccion = lector.GetString(3),
                        Telefono = lector.IsDBNull(4)
                            ? null
                            : lector.GetString(4)
                    };

                    tiendas.Add(idTienda, tienda);
                    resultado.RecojoTienda.Add(tienda);
                }

                if (!lector.IsDBNull(5))
                {
                    tienda.Horarios.Add(new HorarioRecojoDto
                    {
                        DiaSemana = lector.GetString(5),
                        HoraApertura = lector.IsDBNull(6)
                            ? null
                            : lector.GetString(6),
                        HoraCierre = lector.IsDBNull(7)
                            ? null
                            : lector.GetString(7),
                        Cerrado = lector.GetBoolean(8)
                    });
                }
            }
        }

        return Ok(resultado);
    }

    private static object ValorParametro(string? texto)
    {
        return string.IsNullOrWhiteSpace(texto)
            ? DBNull.Value
            : texto.Trim();
    }
}

public class PlazoEntregaDto
{
    public string Departamento { get; set; } = "";
    public string Provincia { get; set; } = "";
    public string Distrito { get; set; } = "";
    public string ModalidadEntrega { get; set; } = "";
    public int DiasMinimos { get; set; }
    public int DiasMaximos { get; set; }
    public decimal CostoEnvio { get; set; }
    public decimal? EnvioGratisDesde { get; set; }
}

public class OpcionesEntregaDto
{
    public Guid CarritoId { get; set; }
    public int CantidadLineas { get; set; }
    public int CantidadUnidades { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DescuentoTotal { get; set; }
    public decimal TotalCarrito { get; set; }

    public List<OpcionDomicilioDto> EnvioDomicilio
        { get; set; } = new();

    public List<TiendaRecojoDto> RecojoTienda
        { get; set; } = new();
}

public class OpcionDomicilioDto
{
    public string Departamento { get; set; } = "";
    public string Provincia { get; set; } = "";
    public string Distrito { get; set; } = "";
    public string Modalidad { get; set; } = "";
    public int DiasMinimos { get; set; }
    public int DiasMaximos { get; set; }
    public decimal CostoBase { get; set; }
    public decimal CostoAplicable { get; set; }
    public decimal? EnvioGratisDesde { get; set; }
    public bool AplicaEnvioGratis { get; set; }
}

public class TiendaRecojoDto
{
    public Guid IdTienda { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Direccion { get; set; } = "";
    public string? Telefono { get; set; }

    public List<HorarioRecojoDto> Horarios
        { get; set; } = new();
}

public class HorarioRecojoDto
{
    public string DiaSemana { get; set; } = "";
    public string? HoraApertura { get; set; }
    public string? HoraCierre { get; set; }
    public bool Cerrado { get; set; }
}
