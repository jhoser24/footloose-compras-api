
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

[ApiController]
[Route("api/v1/pagos")]
public class PagosController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;

    public PagosController(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    // ============================================
    // SERVICIO 35: CONSULTAR MEDIOS DE PAGO
    // ============================================

    [HttpGet("medios")]
    public async Task<ActionResult<ConsultaMediosPagoDto>>
        ConsultarMediosPago(
            [FromQuery] Guid carritoId,
            [FromQuery] string? codigo,
            CancellationToken cancellationToken)
    {
        if (carritoId == Guid.Empty)
        {
            return BadRequest(
                "Debe proporcionar un carritoId valido.");
        }

        var resultado = new ConsultaMediosPagoDto
        {
            CarritoId = carritoId
        };

        await using var conexion =
            await _dataSource.OpenConnectionAsync(
                cancellationToken);

        // Validar carrito activo y sus articulos
        const string sqlCarrito = """
            SELECT
                COALESCE(c.total, 0),
                COUNT(i.id_item)::integer
            FROM public.carritos AS c
            LEFT JOIN public.items_carrito AS i
                ON i.carrito_id = c.id_carrito
            WHERE c.id_carrito = @carritoId
              AND c.estado = 'ACTIVO'
              AND c.responsable_modulo = 'Jorge'
            GROUP BY
                c.id_carrito,
                c.total;
            """;

        await using (var comandoCarrito =
            new NpgsqlCommand(sqlCarrito, conexion))
        {
            comandoCarrito.Parameters.Add(
                "carritoId", NpgsqlDbType.Uuid
            ).Value = carritoId;

            await using var lector =
                await comandoCarrito.ExecuteReaderAsync(
                    cancellationToken);

            if (!await lector.ReadAsync(cancellationToken))
            {
                return NotFound(
                    "No se encontro un carrito activo.");
            }

            resultado.TotalCarrito = lector.GetDecimal(0);
            resultado.CantidadArticulos = lector.GetInt32(1);
        }

        if (resultado.CantidadArticulos == 0)
        {
            return BadRequest(
                "El carrito no contiene articulos.");
        }

        // Consultar medios de pago y sus condiciones
        const string sqlMedios = """
            SELECT
                m.id_medio_pago,
                m.codigo,
                m.nombre,
                m.tipo,
                m.icono_url,
                d.indicaciones,
                d.requisitos,
                d.datos_solicitados,
                d.condiciones,
                d.simulacion_academica
            FROM public.medios_pago AS m
            INNER JOIN public.medios_pago_detalle_jorge AS d
                ON d.id_medio_pago = m.id_medio_pago
            WHERE m.activo = TRUE
              AND m.responsable_modulo = 'Jorge'
              AND d.responsable_modulo = 'Jorge'
              AND (
                  @codigo IS NULL
                  OR m.codigo = @codigo
              )
            ORDER BY m.nombre;
            """;

        await using var comandoMedios =
            new NpgsqlCommand(sqlMedios, conexion);

        comandoMedios.Parameters.Add(
            "codigo", NpgsqlDbType.Varchar
        ).Value = string.IsNullOrWhiteSpace(codigo)
            ? (object)DBNull.Value
            : codigo.Trim().ToUpperInvariant();

        await using var lectorMedios =
            await comandoMedios.ExecuteReaderAsync(
                cancellationToken);

        while (await lectorMedios.ReadAsync(cancellationToken))
        {
            resultado.MediosPago.Add(new MedioPagoConsultaDto
            {
                IdMedioPago = lectorMedios.GetGuid(0),
                Codigo = lectorMedios.GetString(1),
                Nombre = lectorMedios.GetString(2),
                Tipo = lectorMedios.GetString(3),
                IconoUrl = lectorMedios.IsDBNull(4)
                    ? null
                    : lectorMedios.GetString(4),
                Indicaciones = lectorMedios.GetString(5),
                Requisitos = lectorMedios.GetString(6),
                DatosSolicitados = lectorMedios.GetString(7),
                Condiciones = lectorMedios.GetString(8),
                SimulacionAcademica =
                    lectorMedios.GetBoolean(9)
            });
        }

        if (!string.IsNullOrWhiteSpace(codigo)
            && resultado.MediosPago.Count == 0)
        {
            return NotFound(
                "No se encontro el medio de pago solicitado.");
        }

        return Ok(resultado);
    }

    // ============================================
    // SERVICIO 36: SOLICITAR PAGO DE LA COMPRA
    // ============================================

    [HttpPost("solicitudes")]
    public async Task<ActionResult<ResultadoSolicitudPagoDto>>
        SolicitarPagoCompra(
            [FromBody] SolicitudPagoCompraDto? solicitud,
            CancellationToken cancellationToken)
    {
        // 1. Validar informacion recibida
        if (solicitud == null ||
            solicitud.CarritoId == Guid.Empty)
        {
            return BadRequest("Indique un carrito valido.");
        }

        string codigoMedio =
            (solicitud.CodigoMedioPago ?? "")
                .Trim().ToUpperInvariant();

        string modalidad =
            (solicitud.ModalidadEntrega ?? "")
                .Trim().ToUpperInvariant();

        string comprobante =
            (solicitud.TipoComprobante ?? "")
                .Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(codigoMedio))
        {
            return BadRequest(
                "Seleccione un medio de pago.");
        }

        if (modalidad != "ENVIO_DOMICILIO" &&
            modalidad != "RETIRO_TIENDA")
        {
            return BadRequest(
                "Seleccione una modalidad de entrega.");
        }

        if (comprobante != "BOLETA" &&
            comprobante != "FACTURA")
        {
            return BadRequest(
                "Seleccione BOLETA o FACTURA.");
        }

        if (string.IsNullOrWhiteSpace(
                solicitud.NombreComprador) ||
            string.IsNullOrWhiteSpace(
                solicitud.CorreoComprador))
        {
            return BadRequest(
                "Complete los datos del comprador.");
        }

        if (!solicitud.AceptaPoliticaDatos)
        {
            return BadRequest(
                "Debe aceptar la politica de proteccion de datos.");
        }

        await using var conexion =
            await _dataSource.OpenConnectionAsync(
                cancellationToken);

        // 2. Validar carrito activo
        const string sqlCarrito = """
            SELECT
                c.total,
                COUNT(i.id_item)::integer
            FROM public.carritos AS c
            LEFT JOIN public.items_carrito AS i
                ON i.carrito_id = c.id_carrito
            WHERE c.id_carrito = @carritoId
              AND c.estado = 'ACTIVO'
              AND c.responsable_modulo = 'Jorge'
            GROUP BY
                c.id_carrito,
                c.total;
            """;

        decimal totalCarrito;
        int cantidadArticulos;

        await using (var comando =
            new NpgsqlCommand(sqlCarrito, conexion))
        {
            comando.Parameters.Add(
                "carritoId", NpgsqlDbType.Uuid
            ).Value = solicitud.CarritoId;

            await using var lector =
                await comando.ExecuteReaderAsync(
                    cancellationToken);

            if (!await lector.ReadAsync(cancellationToken))
            {
                return NotFound(
                    "No se encontro un carrito activo.");
            }

            totalCarrito = lector.GetDecimal(0);
            cantidadArticulos = lector.GetInt32(1);
        }

        if (cantidadArticulos == 0)
        {
            return BadRequest(
                "El carrito esta vacio.");
        }

        // 3. Validar medio de pago seleccionado
        const string sqlMedio = """
            SELECT
                m.id_medio_pago,
                m.nombre,
                m.tipo,
                d.indicaciones
            FROM public.medios_pago AS m
            INNER JOIN public.medios_pago_detalle_jorge AS d
                ON d.id_medio_pago = m.id_medio_pago
            WHERE m.codigo = @codigo
              AND m.activo = TRUE
              AND m.responsable_modulo = 'Jorge'
              AND d.responsable_modulo = 'Jorge'
              AND d.simulacion_academica = TRUE;
            """;

        Guid idMedioPago;
        string nombreMedio;
        string tipoMedio;
        string indicaciones;

        await using (var comando =
            new NpgsqlCommand(sqlMedio, conexion))
        {
            comando.Parameters.Add(
                "codigo", NpgsqlDbType.Varchar
            ).Value = codigoMedio;

            await using var lector =
                await comando.ExecuteReaderAsync(
                    cancellationToken);

            if (!await lector.ReadAsync(cancellationToken))
            {
                return BadRequest(
                    "Medio de pago no disponible.");
            }

            idMedioPago = lector.GetGuid(0);
            nombreMedio = lector.GetString(1);
            tipoMedio = lector.GetString(2);
            indicaciones = lector.GetString(3);
        }

        // Gift Card no genera una solicitud
        // convencional de pago
        if (tipoMedio == "TARJETA_REGALO")
        {
            return BadRequest(
                "Gift Card se gestiona por el ingreso de un codigo.");
        }

        // 4. Verificar modalidad y costo de entrega
        decimal costoEnvio = 0;

        if (modalidad == "ENVIO_DOMICILIO")
        {
            string tipoEnvio =
                (solicitud.TipoEnvio ?? "")
                    .Trim().ToUpperInvariant();

            if (tipoEnvio != "DELIVERY_REGULAR" &&
                tipoEnvio != "DELIVERY_EXPRESS")
            {
                return BadRequest(
                    "Seleccione DELIVERY_REGULAR o DELIVERY_EXPRESS.");
            }

            if (string.IsNullOrWhiteSpace(
                    solicitud.Departamento) ||
                string.IsNullOrWhiteSpace(
                    solicitud.Provincia) ||
                string.IsNullOrWhiteSpace(
                    solicitud.Distrito))
            {
                return BadRequest(
                    "Complete departamento, provincia y distrito.");
            }

            const string sqlEntrega = """
                SELECT
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
                  AND pe.modalidad_entrega = @tipoEnvio
                  AND LOWER(d.nombre) = LOWER(@departamento)
                  AND LOWER(p.nombre) = LOWER(@provincia)
                  AND LOWER(u.nombre) = LOWER(@distrito)
                LIMIT 1;
                """;

            await using var comando =
                new NpgsqlCommand(sqlEntrega, conexion);

            comando.Parameters.Add(
                "tipoEnvio", NpgsqlDbType.Varchar
            ).Value = tipoEnvio;

            comando.Parameters.Add(
                "departamento", NpgsqlDbType.Text
            ).Value = solicitud.Departamento.Trim();

            comando.Parameters.Add(
                "provincia", NpgsqlDbType.Text
            ).Value = solicitud.Provincia.Trim();

            comando.Parameters.Add(
                "distrito", NpgsqlDbType.Text
            ).Value = solicitud.Distrito.Trim();

            await using var lector =
                await comando.ExecuteReaderAsync(
                    cancellationToken);

            if (!await lector.ReadAsync(cancellationToken))
            {
                return BadRequest(
                    "No existe una opcion de envio para ese destino.");
            }

            decimal costoBase = lector.GetDecimal(0);

            decimal? gratisDesde = lector.IsDBNull(1)
                ? null
                : lector.GetDecimal(1);

            costoEnvio =
                gratisDesde.HasValue &&
                totalCarrito >= gratisDesde.Value
                    ? 0
                    : costoBase;
        }
        else
        {
            // Recojo en tienda
            if (!solicitud.TiendaId.HasValue ||
                solicitud.TiendaId.Value == Guid.Empty)
            {
                return BadRequest(
                    "Seleccione una tienda para el recojo.");
            }

            const string sqlTienda = """
                SELECT EXISTS (
                    SELECT 1
                    FROM public.tiendas
                    WHERE id_tienda = @tiendaId
                      AND activa = TRUE
                      AND permite_retiro_tienda = TRUE
                      AND responsable_modulo = 'Jorge'
                );
                """;

            await using var comando =
                new NpgsqlCommand(sqlTienda, conexion);

            comando.Parameters.Add(
                "tiendaId", NpgsqlDbType.Uuid
            ).Value = solicitud.TiendaId.Value;

            bool tiendaValida = (bool)
                (await comando.ExecuteScalarAsync(
                    cancellationToken) ?? false);

            if (!tiendaValida)
            {
                return BadRequest(
                    "La tienda no esta disponible para recojo.");
            }
        }

        // 5. Calcular el monto de la solicitud simulada
        decimal montoSolicitud =
            totalCarrito + costoEnvio;

        string codigoSimulado =
            "SIM-S36-" +
            Guid.NewGuid()
                .ToString("N")[..12]
                .ToUpperInvariant();

        // 6. Registrar intento de pago academico
        const string sqlInsertar = """
            INSERT INTO public.intentos_pago (
                carrito_id,
                pedido_id,
                medio_pago_id,
                monto,
                moneda,
                codigo_transaccion_simulada,
                estado_pago,
                mensaje_respuesta,
                responsable_modulo
            )
            VALUES (
                @carritoId,
                NULL,
                @medioId,
                @monto,
                'PEN',
                @codigoSimulado,
                'INICIADO',
                'Solicitud academica registrada. Sin cobro real.',
                'Jorge'
            )
            RETURNING id_intento;
            """;

        Guid idIntento;

        await using (var comando =
            new NpgsqlCommand(sqlInsertar, conexion))
        {
            comando.Parameters.Add(
                "carritoId", NpgsqlDbType.Uuid
            ).Value = solicitud.CarritoId;

            comando.Parameters.Add(
                "medioId", NpgsqlDbType.Uuid
            ).Value = idMedioPago;

            comando.Parameters.Add(
                "monto", NpgsqlDbType.Numeric
            ).Value = montoSolicitud;

            comando.Parameters.Add(
                "codigoSimulado", NpgsqlDbType.Varchar
            ).Value = codigoSimulado;

            idIntento = (Guid)
                (await comando.ExecuteScalarAsync(
                    cancellationToken))!;
        }

        // 7. Respuesta del servicio 36
        return StatusCode(
            201,
            new ResultadoSolicitudPagoDto
            {
                IdIntento = idIntento,
                CarritoId = solicitud.CarritoId,
                CodigoTransaccionSimulada = codigoSimulado,
                EstadoPago = "INICIADO",
                MedioPago = nombreMedio,
                TipoComprobante = comprobante,
                TotalCarrito = totalCarrito,
                CostoEnvio = costoEnvio,
                MontoSolicitud = montoSolicitud,
                Moneda = "PEN",
                IndicacionesContinuacion = indicaciones,
                Mensaje =
                    "Solicitud simulada registrada. No se realizo ningun cobro.",
                SimulacionAcademica = true
            }
        );
    }

} // FIN DE PagosController


// ============================================
// CLASES DEL SERVICIO 35
// ============================================

public class ConsultaMediosPagoDto
{
    public Guid CarritoId { get; set; }

    public int CantidadArticulos { get; set; }

    public decimal TotalCarrito { get; set; }

    public List<MedioPagoConsultaDto> MediosPago
        { get; set; } = new();
}

public class MedioPagoConsultaDto
{
    public Guid IdMedioPago { get; set; }

    public string Codigo { get; set; } = "";

    public string Nombre { get; set; } = "";

    public string Tipo { get; set; } = "";

    public string? IconoUrl { get; set; }

    public string Indicaciones { get; set; } = "";

    public string Requisitos { get; set; } = "";

    public string DatosSolicitados { get; set; } = "";

    public string Condiciones { get; set; } = "";

    public bool SimulacionAcademica { get; set; }
}


// ============================================
// CLASES DEL SERVICIO 36
// ============================================

public class SolicitudPagoCompraDto
{
    public Guid CarritoId { get; set; }

    public string CodigoMedioPago { get; set; } = "";

    public string ModalidadEntrega { get; set; } = "";

    public string? TipoEnvio { get; set; }

    public string? Departamento { get; set; }

    public string? Provincia { get; set; }

    public string? Distrito { get; set; }

    public Guid? TiendaId { get; set; }

    public string TipoComprobante { get; set; } = "";

    public string NombreComprador { get; set; } = "";

    public string CorreoComprador { get; set; } = "";

    public bool AceptaPoliticaDatos { get; set; }

    public bool AutorizaPromociones { get; set; }
}

public class ResultadoSolicitudPagoDto
{
    public Guid IdIntento { get; set; }

    public Guid CarritoId { get; set; }

    public string CodigoTransaccionSimulada
        { get; set; } = "";

    public string EstadoPago { get; set; } = "";

    public string MedioPago { get; set; } = "";

    public string TipoComprobante { get; set; } = "";

    public decimal TotalCarrito { get; set; }

    public decimal CostoEnvio { get; set; }

    public decimal MontoSolicitud { get; set; }

    public string Moneda { get; set; } = "";

    public string IndicacionesContinuacion
        { get; set; } = "";

    public string Mensaje { get; set; } = "";

    public bool SimulacionAcademica { get; set; }
}
