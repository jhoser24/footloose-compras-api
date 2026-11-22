
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

[ApiController]
[Route("api/v1/pedidos")]
public class PedidosController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;

    public PedidosController(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    // ============================================
    // SERVICIO 37:
    // CONSULTAR HISTORIAL DE PEDIDOS DEL CLIENTE
    // ============================================

    [HttpGet]
    public async Task<ActionResult<HistorialPedidosDto>>
        ConsultarHistorialPedidos(
            [FromQuery] string? correoCliente,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(correoCliente))
        {
            return BadRequest(
                "Debe proporcionar el correo del cliente.");
        }

        string correo = correoCliente.Trim().ToLowerInvariant();

        if (correo != "cliente.s37@example.com")
        {
            return NotFound(
                "Cliente academico no disponible para esta consulta.");
        }

        var resultado = new HistorialPedidosDto
        {
            CorreoCliente = correo
        };

        const string sql = """
            SELECT
                c.id_cliente,
                c.nombres,
                c.apellidos,
                p.id_pedido,
                p.numero_pedido,
                p.fecha_pedido,
                p.estado_pedido,
                p.modalidad_entrega,
                p.subtotal,
                p.descuento,
                p.costo_envio,
                p.total,
                d.id_detalle,
                d.variante_id,
                d.cantidad,
                d.precio_unitario,
                d.subtotal
            FROM public.clientes AS c
            INNER JOIN public.pedidos AS p
                ON p.cliente_id = c.id_cliente
            LEFT JOIN public.detalles_pedido AS d
                ON d.pedido_id = p.id_pedido
               AND d.responsable_modulo = 'Jorge'
            WHERE c.email = @correo
              AND c.responsable_modulo = 'Jorge'
              AND p.responsable_modulo = 'Jorge'
            ORDER BY
                p.fecha_pedido DESC,
                p.numero_pedido,
                d.id_detalle;
            """;

        await using var conexion =
            await _dataSource.OpenConnectionAsync(
                cancellationToken);

        await using var comando =
            new NpgsqlCommand(sql, conexion);

        comando.Parameters.Add(
            "correo", NpgsqlDbType.Varchar
        ).Value = correo;

        var pedidos = new Dictionary<Guid, PedidoHistorialDto>();

        await using var lector =
            await comando.ExecuteReaderAsync(
                cancellationToken);

        while (await lector.ReadAsync(cancellationToken))
        {
            Guid idPedido = lector.GetGuid(3);

            if (!pedidos.TryGetValue(idPedido, out var pedido))
            {
                resultado.IdCliente = lector.GetGuid(0);
                resultado.NombresCliente = lector.GetString(1);
                resultado.ApellidosCliente = lector.GetString(2);

                pedido = new PedidoHistorialDto
                {
                    IdPedido = idPedido,
                    NumeroPedido = lector.GetString(4),
                    FechaPedido = lector.GetDateTime(5),
                    EstadoPedido = lector.GetString(6),
                    ModalidadEntrega = lector.IsDBNull(7)
                        ? null
                        : lector.GetString(7),
                    Subtotal = lector.GetDecimal(8),
                    Descuento = lector.GetDecimal(9),
                    CostoEnvio = lector.GetDecimal(10),
                    Total = lector.GetDecimal(11)
                };

                pedidos.Add(idPedido, pedido);
                resultado.Pedidos.Add(pedido);
            }

            if (!lector.IsDBNull(12))
            {
                int cantidad = lector.GetInt32(14);

                pedido.Detalles.Add(new DetallePedidoDto
                {
                    IdDetalle = lector.GetGuid(12),
                    VarianteId = lector.GetInt32(13),
                    Cantidad = cantidad,
                    PrecioUnitario = lector.GetDecimal(15),
                    Subtotal = lector.GetDecimal(16)
                });

                pedido.CantidadUnidades += cantidad;
            }
        }

        resultado.CantidadPedidos = resultado.Pedidos.Count;

        resultado.Mensaje = resultado.CantidadPedidos == 0
            ? "El cliente no tiene pedidos registrados."
            : "Historial de pedidos consultado correctamente.";

        return Ok(resultado);
    }

    // ============================================
    // SERVICIO 38:
    // CONSULTAR SEGUIMIENTO DE UN PEDIDO
    // ============================================

    [HttpGet("seguimiento/{numeroPedido}")]
    public async Task<ActionResult<SeguimientoPedidoDto>>
        ConsultarSeguimientoPedido(
            [FromRoute] string numeroPedido,
            CancellationToken cancellationToken)
    {
        // 1. Validar el numero de pedido
        if (string.IsNullOrWhiteSpace(numeroPedido))
        {
            return BadRequest(
                "Debe proporcionar un numero de pedido.");
        }

        string numero = numeroPedido.Trim();

        // 2. Consultar un pedido del cliente academico
        const string sqlSeguimiento = """
            SELECT
                p.numero_pedido,
                p.fecha_pedido,
                p.estado_pedido,
                p.modalidad_entrega,
                p.total
            FROM public.pedidos AS p
            INNER JOIN public.clientes AS c
                ON c.id_cliente = p.cliente_id
            WHERE p.numero_pedido = @numeroPedido
              AND p.responsable_modulo = 'Jorge'
              AND c.responsable_modulo = 'Jorge'
              AND c.email = 'cliente.s37@example.com'
            LIMIT 1;
            """;

        await using var conexion =
            await _dataSource.OpenConnectionAsync(
                cancellationToken);

        await using var comando =
            new NpgsqlCommand(sqlSeguimiento, conexion);

        comando.Parameters.Add(
            "numeroPedido", NpgsqlDbType.Varchar
        ).Value = numero;

        await using var lector =
            await comando.ExecuteReaderAsync(
                cancellationToken);

        // 3. Responder cuando no exista el pedido
        if (!await lector.ReadAsync(cancellationToken))
        {
            return NotFound(
                "No se encontro el pedido solicitado.");
        }

        // 4. Recuperar el estado real de la BD
        string estado = lector.GetString(2);

        string descripcion = estado switch
        {
            "PENDIENTE_PAGO" =>
                "El pedido figura como pendiente de pago.",

            "PAGADO" =>
                "El pedido figura como pagado.",

            "PREPARANDO" =>
                "El pedido figura en preparacion.",

            "EN_CAMINO" =>
                "El pedido figura en camino.",

            "ENTREGADO" =>
                "El pedido figura como entregado.",

            "CANCELADO" =>
                "El pedido figura como cancelado.",

            _ =>
                "No existe una descripcion para el estado."
        };

        // 5. Organizar la respuesta del seguimiento
        var resultado = new SeguimientoPedidoDto
        {
            NumeroPedido = lector.GetString(0),
            FechaPedido = lector.GetDateTime(1),
            EstadoPedido = estado,
            ModalidadEntrega = lector.IsDBNull(3)
                ? null
                : lector.GetString(3),
            Total = lector.GetDecimal(4),
            DescripcionEstado = descripcion,
            SimulacionAcademica = true
        };

        return Ok(resultado);
    }
}


// ============================================
// CLASES DEL SERVICIO 37
// ============================================

public class HistorialPedidosDto
{
    public Guid? IdCliente { get; set; }

    public string CorreoCliente { get; set; } = "";

    public string? NombresCliente { get; set; }

    public string? ApellidosCliente { get; set; }

    public int CantidadPedidos { get; set; }

    public string Mensaje { get; set; } = "";

    public List<PedidoHistorialDto> Pedidos
        { get; set; } = new();
}

public class PedidoHistorialDto
{
    public Guid IdPedido { get; set; }

    public string NumeroPedido { get; set; } = "";

    public DateTime FechaPedido { get; set; }

    public string EstadoPedido { get; set; } = "";

    public string? ModalidadEntrega { get; set; }

    public decimal Subtotal { get; set; }

    public decimal Descuento { get; set; }

    public decimal CostoEnvio { get; set; }

    public decimal Total { get; set; }

    public int CantidadUnidades { get; set; }

    public List<DetallePedidoDto> Detalles
        { get; set; } = new();
}

public class DetallePedidoDto
{
    public Guid IdDetalle { get; set; }

    public int VarianteId { get; set; }

    public int Cantidad { get; set; }

    public decimal PrecioUnitario { get; set; }

    public decimal Subtotal { get; set; }
}


// ============================================
// CLASE DEL SERVICIO 38
// ============================================

public class SeguimientoPedidoDto
{
    public string NumeroPedido { get; set; } = "";

    public DateTime FechaPedido { get; set; }

    public string EstadoPedido { get; set; } = "";

    public string? ModalidadEntrega { get; set; }

    public decimal Total { get; set; }

    public string DescripcionEstado { get; set; } = "";

    public bool SimulacionAcademica { get; set; }
}
