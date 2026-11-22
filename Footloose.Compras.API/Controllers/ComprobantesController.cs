
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

[ApiController]
[Route("api/v1/comprobantes")]
public class ComprobantesController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;

    public ComprobantesController(
        NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    // ============================================
    // SERVICIO 39:
    // CONSULTAR COMPROBANTES ELECTRONICOS
    // ============================================

    [HttpGet]
    public async Task<ActionResult<ConsultaComprobanteDto>>
        ConsultarComprobantesElectronicos(
            [FromQuery] string? tipo,
            [FromQuery] string? serie,
            [FromQuery] string? numero,
            [FromQuery] DateOnly? fechaEmision,
            [FromQuery] decimal? montoTotal,
            [FromQuery] string? formato,
            CancellationToken cancellationToken)
    {
        // 1. Validar los campos de busqueda
        if (string.IsNullOrWhiteSpace(tipo) ||
            string.IsNullOrWhiteSpace(serie) ||
            string.IsNullOrWhiteSpace(numero))
        {
            return BadRequest(
                "Debe ingresar tipo, serie y numero.");
        }

        string tipoNormalizado =
            tipo.Trim().ToUpperInvariant();

        string serieNormalizada =
            serie.Trim().ToUpperInvariant();

        string numeroNormalizado = numero.Trim();

        if (tipoNormalizado != "BOLETA" &&
            tipoNormalizado != "FACTURA")
        {
            return BadRequest(
                "El tipo debe ser BOLETA o FACTURA.");
        }

        string? formatoNormalizado =
            string.IsNullOrWhiteSpace(formato)
                ? null
                : formato.Trim().ToUpperInvariant();

        if (formatoNormalizado != null &&
            formatoNormalizado != "PDF" &&
            formatoNormalizado != "XML")
        {
            return BadRequest(
                "El formato debe ser PDF o XML.");
        }

        if (montoTotal.HasValue && montoTotal.Value < 0)
        {
            return BadRequest(
                "El monto no puede ser negativo.");
        }

        // 2. Consultar el comprobante en Supabase
        const string sql = """
            SELECT
                c.id_comprobante,
                p.numero_pedido,
                c.tipo_comprobante,
                c.serie,
                c.correlativo,
                c.monto_total,
                c.fecha_emision,
                c.ruc_cliente,
                c.razon_social_cliente
            FROM public.comprobantes AS c
            INNER JOIN public.pedidos AS p
                ON p.id_pedido = c.pedido_id
            WHERE c.responsable_modulo = 'Jorge'
              AND p.responsable_modulo = 'Jorge'
              AND c.tipo_comprobante = @tipo
              AND c.serie = @serie
              AND c.correlativo = @numero
              AND (
                  @fechaEmision IS NULL
                  OR (
                      c.fecha_emision
                      AT TIME ZONE 'UTC'
                  )::date = @fechaEmision
              )
              AND (
                  @montoTotal IS NULL
                  OR c.monto_total = @montoTotal
              )
            LIMIT 1;
            """;

        // 3. Abrir conexion con PostgreSQL
        await using var conexion =
            await _dataSource.OpenConnectionAsync(
                cancellationToken);

        await using var comando =
            new NpgsqlCommand(sql, conexion);

        // 4. Asignar los parametros SQL
        comando.Parameters.Add(
            "tipo", NpgsqlDbType.Varchar
        ).Value = tipoNormalizado;

        comando.Parameters.Add(
            "serie", NpgsqlDbType.Varchar
        ).Value = serieNormalizada;

        comando.Parameters.Add(
            "numero", NpgsqlDbType.Varchar
        ).Value = numeroNormalizado;

        comando.Parameters.Add(
            "fechaEmision", NpgsqlDbType.Date
        ).Value = fechaEmision.HasValue
            ? (object)fechaEmision.Value
            : DBNull.Value;

        comando.Parameters.Add(
            "montoTotal", NpgsqlDbType.Numeric
        ).Value = montoTotal.HasValue
            ? (object)montoTotal.Value
            : DBNull.Value;

        // 5. Ejecutar consulta
        await using var lector =
            await comando.ExecuteReaderAsync(
                cancellationToken);

        if (!await lector.ReadAsync(cancellationToken))
        {
            return NotFound(
                "No se encontro un comprobante academico " +
                "con los datos proporcionados.");
        }

        // 6. Construir respuesta JSON
        var resultado = new ConsultaComprobanteDto
        {
            IdComprobante = lector.GetGuid(0),
            NumeroPedido = lector.GetString(1),
            TipoComprobante = lector.GetString(2),
            Serie = lector.GetString(3),
            Numero = lector.GetString(4),
            MontoTotal = lector.GetDecimal(5),
            FechaEmisionUtc = lector.GetDateTime(6),

            RucCliente = lector.IsDBNull(7)
                ? null
                : lector.GetString(7),

            RazonSocialCliente = lector.IsDBNull(8)
                ? null
                : lector.GetString(8),

            FormatoSolicitado = formatoNormalizado,

            DocumentoDisponible = false,

            Mensaje =
                "Comprobante academico encontrado. " +
                "No existe un PDF o XML tributario generado.",

            SimulacionAcademica = true
        };

        return Ok(resultado);
    }
}


// ============================================
// CLASE DE RESPUESTA DEL SERVICIO 39
// ============================================

public class ConsultaComprobanteDto
{
    public Guid IdComprobante { get; set; }

    public string NumeroPedido { get; set; } = "";

    public string TipoComprobante { get; set; } = "";

    public string Serie { get; set; } = "";

    public string Numero { get; set; } = "";

    public decimal MontoTotal { get; set; }

    public DateTime FechaEmisionUtc { get; set; }

    public string? RucCliente { get; set; }

    public string? RazonSocialCliente { get; set; }

    public string? FormatoSolicitado { get; set; }

    public bool DocumentoDisponible { get; set; }

    public string Mensaje { get; set; } = "";

    public bool SimulacionAcademica { get; set; }
}
