-- La API controla la transacción del lote. Un error SQL interrumpe la operación.
SET XACT_ABORT ON;
SET NOCOUNT ON;

IF @origen=@destino OR EXISTS (
    SELECT 1 FROM dbo.TFC_Productos_Agrupados_RGB WITH (UPDLOCK)
    WHERE id_producto IN (@origen, @destino))
    THROW 51001, 'El origen o el destino ya está agrupado, o ambos son iguales.', 1;

IF NOT EXISTS (
    SELECT 1 FROM dbo.entidad WHERE id_entidad=@entidad AND Tipo=1 AND gana_secuencia='001')
    OR (SELECT COUNT(*) FROM dbo.tfc_productos_rgb WITH (UPDLOCK)
        WHERE id_producto_s=@origen AND id_entidad=@entidad) <> 1
    THROW 51002, 'La relación del producto con el proveedor de origen no es válida o es ambigua.', 1;

DECLARE @codigo char(20), @descripcion varchar(500);
SELECT TOP (1) @codigo=r.codigo_prod_p, @descripcion=r.descrip_prod_p
FROM dbo.tfc_productos_rgb r WITH (UPDLOCK)
JOIN dbo.Productos p ON p.ID_Producto=r.id_producto_s AND COALESCE(p.activo,1)=1
JOIN dbo.entidad e ON e.id_entidad=r.id_entidad
WHERE r.id_producto_s=@destino AND r.id_prioridad=1 AND NULLIF(TRIM(r.codigo_prod_p),'') IS NOT NULL

  AND EXISTS (SELECT 1 FROM dbo.tfc_vista_maestro_productos v
              WHERE v.codigo_prod=p.Codigo_Prod)
ORDER BY r.id_entidad, r.codigo_prod_p, r.id_empresa;
IF @codigo IS NULL
    THROW 51003, 'El destino debe ser un producto principal activo.', 1;

-- La vista excluye los códigos registrados como hijos en esta tabla.
INSERT INTO dbo.TFC_Productos_Agrupados_RGB (id_producto,id_producto_madre,fecha_modificacion)
VALUES (@origen,@destino,GETDATE());

-- Reasigna también los hijos que ya consolidaban su rotación en el origen.
UPDATE dbo.TFC_Productos_Agrupados_Finales_RGB
SET id_producto_madre=@destino, fecha_modificacion=GETDATE()
WHERE id_producto=@origen OR id_producto_madre=@origen;
IF NOT EXISTS (SELECT 1 FROM dbo.TFC_Productos_Agrupados_Finales_RGB WHERE id_producto=@origen)
    INSERT INTO dbo.TFC_Productos_Agrupados_Finales_RGB (id_producto,id_producto_madre,fecha_modificacion)
    VALUES (@origen,@destino,GETDATE());

-- Traslada la relación como proveedor secundario y conserva su precio.
UPDATE dbo.tfc_productos_rgb
SET id_prioridad=2, codigo_prod_p=@codigo, descrip_prod_p=@descripcion,
    id_producto_a=@destino, id_producto_s=@destino
WHERE id_producto_s=@origen AND id_entidad=@entidad;

-- Si alguna bodega tiene saldo positivo, suma cada saldo al destino y deja el origen en cero.
DECLARE @e float=0, @t2 float=0, @t7 float=0, @l3 float=0;
SELECT @e=COALESCE(stocke,0), @t2=COALESCE(stockt2,0),
       @t7=COALESCE(stockt7,0), @l3=COALESCE(stockl3,0)
FROM dbo.tfc_vista_maestro_productos_stock WITH (UPDLOCK) WHERE id_producto=@origen;
IF @e>0 OR @t2>0 OR @t7>0 OR @l3>0
BEGIN
    UPDATE dbo.tfc_vista_maestro_productos_stock WITH (UPDLOCK)
    SET stocke=COALESCE(stocke,0)+@e, stockt2=COALESCE(stockt2,0)+@t2,
        stockt7=COALESCE(stockt7,0)+@t7, stockl3=COALESCE(stockl3,0)+@l3
    WHERE id_producto=@destino;
    IF @@ROWCOUNT=0
        INSERT INTO dbo.tfc_vista_maestro_productos_stock (id_producto,stocke,stockt2,stockt7,stockl3)
        VALUES (@destino,@e,@t2,@t7,@l3);
    UPDATE dbo.tfc_vista_maestro_productos_stock SET stocke=0,stockt2=0,stockt7=0,stockl3=0
    WHERE id_producto=@origen;
END;


