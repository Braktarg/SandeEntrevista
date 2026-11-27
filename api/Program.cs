using System.Data;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Sande")
    ?? throw new InvalidOperationException("Configure ConnectionStrings__Sande.");
// El script de guardado es fijo. Cada ejecución enlaza los IDs como parámetros SQL.
var mergeSql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Unificar.sql"));
var app = builder.Build();

app.UseExceptionHandler(handler => handler.Run(async context =>
    await Results.Problem("No se pudo completar la operación.").ExecuteAsync(context)));

app.MapGet("/api/proveedores", async (string? q, CancellationToken ct) =>
    await Query("""
        SELECT TOP (30) id_entidad idEntidad, RTRIM(Gana_rut) rut, RTRIM(abreviacion) abreviacion, razon_social razonSocial
        FROM dbo.entidad WHERE Tipo=1 AND gana_secuencia='001'
          AND (CHARINDEX(@q, abreviacion)>0 OR CHARINDEX(@q, razon_social)>0 OR @q='')
        ORDER BY abreviacion, id_entidad
        """, ct, ("@q", q?.Trim() ?? "")));

app.MapGet("/api/marcas", async (string? q, int? idEntidad, CancellationToken ct) =>
    await Query("""
        SELECT TOP (30) m.MAR_Codigo idMarca, RTRIM(m.MAR_Descripcion) marca
        FROM dbo.marcas m WHERE (CHARINDEX(@q, m.MAR_Descripcion)>0 OR @q='')
          AND (@entity IS NULL OR EXISTS (
            SELECT 1 FROM dbo.Productos p JOIN dbo.tfc_productos_rgb r ON r.id_producto_s=p.ID_Producto
            WHERE p.ID_Marca=m.MAR_Codigo AND r.id_entidad=@entity))
        ORDER BY m.MAR_Descripcion, m.MAR_Codigo
        """, ct, ("@q", q?.Trim() ?? ""), ("@entity", idEntidad)));

app.MapGet("/api/productos/consulta", async (int idEntidad, string? marca, int? minClientes,
    string? tipos, string? situaciones, CancellationToken ct) =>
{
    if (idEntidad <= 0 || minClientes < 0) return Results.BadRequest(new { message = "Filtros inválidos." });
    var items = await Query("""
        SELECT DISTINCT p.ID_Producto idProducto, r.id_entidad idEntidad, RTRIM(v.seccion) seccion,
          RTRIM(v.categoria) categoria, RTRIM(v.codigo_prod) codigo, RTRIM(v.code) code,
          RTRIM(v.descrip_prod) producto, v.ncli nClientes, RTRIM(v.tipo_producto) tipo,
          RTRIM(v.id_situacion) situacion, RTRIM(v.fib) fui, RTRIM(v.marca) marca,
          COALESCE(s.stocke,0)+COALESCE(s.stockt2,0)+COALESCE(s.stockt7,0)+COALESCE(s.stockl3,0) stockTotal,
          CAST(NULL AS varchar(15)) codigoN
        FROM dbo.tfc_vista_maestro_productos v
        JOIN dbo.Productos p ON p.Codigo_Prod=v.codigo_prod
        JOIN dbo.tfc_productos_rgb r ON r.id_producto_s=p.ID_Producto AND r.codigo_prod_p=v.code
        JOIN dbo.entidad e ON e.id_entidad=r.id_entidad AND e.abreviacion=v.brand
        LEFT JOIN dbo.tfc_vista_maestro_productos_stock s ON s.id_producto=p.ID_Producto
        WHERE r.id_entidad=@entity AND (@brand='' OR v.marca=@brand)
          AND COALESCE(v.ncli,0)>=@minimum
          AND (@types='' OR RTRIM(v.tipo_producto) IN (SELECT TRIM(value) FROM STRING_SPLIT(@types,',')))
          AND (@situations='' OR RTRIM(v.id_situacion) IN (SELECT TRIM(value) FROM STRING_SPLIT(@situations,',')))
        ORDER BY codigo, code
        """, ct, ("@entity", idEntidad), ("@brand", marca?.Trim() ?? ""), ("@minimum", minClientes ?? 0),
        ("@types", tipos ?? ""), ("@situations", situaciones ?? ""));
    return Results.Ok(new { total = items.Count, items });
});

// El ejemplo RC506 figura en la vista y tiene prioridad 1, aunque su campo activo es NULL.
app.MapGet("/api/productos/principales/buscar", async (string? q, int? excluirId, CancellationToken ct) =>
    await Query("""
        SELECT DISTINCT TOP (30) p.ID_Producto idProducto, RTRIM(p.Codigo_Prod) codigo,
            RTRIM(r.codigo_prod_p) code, RTRIM(e.abreviacion) proveedor, RTRIM(p.Descrip_Prod) producto
        FROM dbo.Productos p
        JOIN dbo.tfc_productos_rgb r ON r.id_producto_s=p.ID_Producto AND r.id_prioridad=1
        JOIN dbo.entidad e ON e.id_entidad=r.id_entidad
        WHERE COALESCE(p.activo,1)=1 AND p.ID_Producto<>@exclude
          AND NULLIF(TRIM(r.codigo_prod_p),'') IS NOT NULL
          AND EXISTS (SELECT 1 FROM dbo.tfc_vista_maestro_productos v WHERE v.codigo_prod=p.Codigo_Prod)
          AND (CHARINDEX(@q,p.Codigo_Prod)>0 OR CHARINDEX(@q,p.Descrip_Prod)>0 OR @q='')
        ORDER BY codigo, proveedor
        """, ct, ("@exclude", excluirId ?? 0), ("@q", q?.Trim() ?? "")));

app.MapPost("/api/productos/unificar", async (Batch batch, CancellationToken ct) =>
{
    var rows = batch.Unificaciones;
    if (rows is null || rows.Count is 0 or > 500 || rows.Any(row => row is null ||
        row.IdProductoOrigen <= 0 || row.IdProductoDestino <= 0 || row.IdEntidadOrigen <= 0))
        return Results.BadRequest(new { message = "Envíe entre 1 y 500 asignaciones válidas." });

    // Un destino que también es origen haría depender el resultado del orden del lote.
    var origins = rows.Select(row => row.IdProductoOrigen).ToHashSet();
    if (origins.Count != rows.Count || rows.Any(row => origins.Contains(row.IdProductoDestino)))
        return Results.BadRequest(new { message = "Cada origen debe ser único y ningún destino puede ser un origen del mismo lote." });

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync(ct);
    // El aislamiento serializable protege las validaciones frente a guardados concurrentes.
    await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
        foreach (var row in rows.OrderBy(row => row.IdProductoOrigen))
        {
            await using var command = new SqlCommand(mergeSql, connection, transaction);
            command.Parameters.Add("@origen", SqlDbType.Int).Value = row.IdProductoOrigen;
            command.Parameters.Add("@destino", SqlDbType.Int).Value = row.IdProductoDestino;
            command.Parameters.Add("@entidad", SqlDbType.Int).Value = row.IdEntidadOrigen;
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return Results.Ok(new { unificados = rows.Count });
    }
    catch (SqlException ex) when (ex.Number is 51001 or 51002 or 51003 or 2601 or 2627 or 1205)
    {
        if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
        return Results.Conflict(new { message = ex.Number is >= 51001 and <= 51003
            ? ex.Message : "El lote entró en conflicto con los datos existentes. Vuelva a consultar." });
    }
});

app.Run();

// Los alias del SELECT se usan como claves JSON y los filtros se enlazan con tipos SQL.
async Task<List<Dictionary<string, object?>>> Query(string sql, CancellationToken ct,
    params (string Name, object? Value)[] parameters)
{
    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync(ct);
    await using var command = new SqlCommand(sql, connection);
    foreach (var (name, value) in parameters)
        command.Parameters.Add(name, value is null or int ? SqlDbType.Int : SqlDbType.NVarChar,
            value is string ? -1 : 0).Value = value ?? DBNull.Value;
    await using var reader = await command.ExecuteReaderAsync(ct);
    var rows = new List<Dictionary<string, object?>>();
    while (await reader.ReadAsync(ct))
    {
        var record = new Dictionary<string, object?>();
        for (var i = 0; i < reader.FieldCount; i++)
            record[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        rows.Add(record);
    }
    return rows;
}

record Assignment(int IdProductoOrigen, int IdEntidadOrigen, int IdProductoDestino);
record Batch(List<Assignment>? Unificaciones);


