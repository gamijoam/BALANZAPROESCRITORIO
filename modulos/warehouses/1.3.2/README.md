# Almacenes y traslados 1.3.2

Este release corrige el filtro de existencias de Inventario para que corresponda al almacén elegido.

## Qué cambió

- La pantalla anfitriona obtiene los productos sin prefiltrar por existencia global.
- Almacenes reemplaza la cantidad global por el saldo del almacén seleccionado. El filtro «Con existencia»/«Agotados» se aplica después a esa cantidad.
- «Todos», o no tener la extensión activa, conserva el saldo global.
- No cambia ventas, endpoints, permisos ni datos de inventario. No agrega migraciones: conserva `warehouses:0001`.

## Archivos de este release

- `warehouses-1.3.2.zip`: módulo firmado para instalar desde **Administración → Módulos**.
- `publisher-public.pem`: clave pública del firmante. No es una clave privada.

La corrección también requiere el POS anfitrión actualizado, porque el orden de aplicación de filtros vive en la pantalla base.

## Instalación

1. Cierra el POS en el servidor y las terminales.
2. Actualiza el POS anfitrión en el servidor y en cada terminal con una publicación que incluya el cambio descrito arriba. El paquete del POS se genera aparte con `scripts/Publish-LocalPos.ps1`; no está incluido en este ZIP del módulo.
3. Abre el POS actualizado en el servidor. En **Administración → Módulos**, valida e instala `warehouses-1.3.2.zip` desde este directorio. Si el servidor aún no confía en el publicador, registra `publisher-public.pem` según `docs/security/publicar-modulos.md`.
4. Pulsa **Reiniciar servidor y cargar módulos** y espera a que el estado del módulo indique que está activo.
5. Abre el POS en las terminales. En Inventario, elige un almacén y verifica que el saldo y «Con existencia»/«Agotados» correspondan a ese almacén.

No vuelvas a cargar el módulo si ya aparece activo en versión 1.3.2. El paquete no requiere ejecutar SQL ni cambiar la clave de PostgreSQL.

## Integridad y firma

- ID interno: `warehouses`
- Nombre: Almacenes y traslados
- Versión: `1.3.2`
- Firmante: `pos-warehouses-dev-2026`
- SHA-256 del ZIP: `95947DD4F94F5028447ABAF0A5A6C36DD970E7B64DA9BF030A0560BE4FEBC18D`

Verifica desde la raíz del repositorio con PowerShell:

```powershell
.\scripts\Test-SignedModulePackage.ps1 `
  -PackagePath .\modulos\warehouses\1.3.2\warehouses-1.3.2.zip `
  -PublicKeyPath .\modulos\warehouses\1.3.2\publisher-public.pem
```

La auditoría y los criterios están en [`hito-18-inventario-stock-por-almacen.md`](../../../docs/specs/hito-18-inventario-stock-por-almacen.md). La guía de desarrollo de módulos está en [`crear-modulos-pos.md`](../../../docs/manuales/crear-modulos-pos.md).
