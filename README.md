# Prueba técnica SANDE

Este proyecto permite buscar productos duplicados y unificarlos en un solo código. Al guardar, el stock del duplicado pasa al código principal.

Usé Angular con Angular Material, una API en C# con .NET 8 y SQL Server 2022. Angular muestra el formulario y la API valida las asignaciones antes de guardar. SQL Server ejecuta el lote en una sola transacción para que la agrupación, el proveedor y el stock se actualicen juntos.

## Cómo levantar el proyecto

Necesitas Node.js 22.12 o superior dentro de la versión 22, el SDK de .NET 8 y acceso a la base de datos de la prueba.

En PowerShell, desde la carpeta del proyecto, levanta la API. Cuando se te pida la cadena de conexión, usa la del enunciado.

```powershell
$env:ConnectionStrings__Sande = Read-Host 'Cadena de conexión SQL Server'
dotnet run --project api/Sande.Api.csproj -- --urls http://localhost:5080
```

En otra terminal, desde la misma carpeta:

```powershell
cd web
npm ci
npm start
```

Abre http://localhost:4200.

## Cómo probarlo

1. Selecciona el proveedor D-OTERO D y escribe ENERPAC en Marca.
2. Pulsa Buscar y busca la fila RC506-O.
3. En Código N, escribe RC506 y selecciónalo de la lista.

Probé el guardado completo en Edge con SQL Server 2022 local. RC506 quedó con stock E=3, T2=1, T7=2 y L3=0. RC506-O quedó en cero y D-OTERO D pasó a prioridad 2. La base publicada se usó solo para consultas.
