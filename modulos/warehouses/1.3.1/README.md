# Almacenes y traslados 1.3.1

Módulo piloto funcional del POS. ID interno: `warehouses`. Nombre visible: **Almacenes y traslados**. Proyecto fuente: `src/Modules/Pos.Modules.Warehouses` y `src/Modules/Pos.Modules.Warehouses.Desktop`.

## Funciones incluidas

- Existencias separadas por almacén y almacén principal.
- Asignación de almacén a terminal; ventas y devoluciones usan ese saldo desde el servidor.
- Traslados con selección explícita de productos, borrador, despacho, recepción, historial y variaciones.
- Pantalla WPF del módulo y filtro por almacén en Inventario.
- Navegación del compositor organizada en dos paneles; cantidades presentadas sin separadores ambiguos.
- Permisos de consulta, administración y traslado, validados por el servidor.

No agrega compras, proveedores, lotes, vencimientos, series ni sucursales sincronizadas.

## Instalar

1. Confirma que el POS anfitrión y las terminales reportan versiones compatibles con `hostApi`, `clientApi` y `desktopApi` del manifiesto.
2. En la PC servidor, abre **Administración → Módulos** y valida `warehouses-1.3.1.zip`.
3. Revisa dependencias, permisos, compatibilidad y migraciones. La versión 1.3.1 conserva `warehouses:0001` y no incorpora otra migración.
4. Instala desde la aplicación. El servidor debe crear y verificar un respaldo antes de activar.
5. Usa **Reiniciar servidor y cargar módulos**; confirma el estado `Active` y que no aparezca diagnóstico.
6. Verifica el menú **Módulos → Almacenes** con un operador autorizado.

El servidor debe confiar en el publicador del paquete. La clave pública correspondiente está en `publisher-public.pem`; es pública y puede instalarse en el almacén de confianza según [la guía de firma](../../../docs/security/publicar-modulos.md). Nunca distribuyas ni guardes la clave privada junto al ZIP.

## Actualizar después

Publica el mismo ID `warehouses` con una versión superior, conserva las migraciones existentes y agrega otra solo si el esquema cambia. Instala el nuevo ZIP desde **Administración → Módulos**, deja que el servidor cree el respaldo/aplique migraciones y reinícialo para cargar la actualización. No borres los releases anteriores.

## Integridad del archivo

- Archivo: `warehouses-1.3.1.zip`
- Versión del manifiesto: `1.3.1`
- Firma: publicador `pos-warehouses-dev-2026`
- SHA-256: `CEE6C7C4291880C9EC4EA2ECBB4F5EFD415A4BF4EF1F5100CD0577F1422D7321`

Verificación reproducible desde la raíz del repo:

```powershell
.\scripts\Test-SignedModulePackage.ps1 `
  -PackagePath .\modulos\warehouses\1.3.1\warehouses-1.3.1.zip `
  -PublicKeyPath .\modulos\warehouses\1.3.1\publisher-public.pem
```

La fuente de verdad para desarrollar es `src/Modules/Pos.Modules.Warehouses`; las instrucciones completas están en [`docs/manuales/MODULOS-POS.md`](../../../docs/manuales/MODULOS-POS.md) y [`docs/manuales/crear-modulos-pos.md`](../../../docs/manuales/crear-modulos-pos.md).
