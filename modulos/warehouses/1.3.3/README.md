# Almacenes y traslados 1.3.3

Incluye en el historial por producto los despachos y recepciones confirmados. No crea una migración nueva; conserva `warehouses:0001`.

## Instalación

1. Actualiza el POS anfitrión en el servidor y en las terminales con la versión que incluya Desktop API 1.1.0. El instalador del POS anfitrión se genera aparte con `scripts/Publish-LocalPos.ps1`; no forma parte de este ZIP de módulo.
2. Reinicia el servicio POS y abre el POS actualizado.
3. En Administración → Módulos, valida e instala `warehouses-1.3.3.zip`.
4. Si el módulo ya estaba instalado, conserva los datos y actualiza su versión; después reinicia servidor y terminales para cargar el nuevo contrato de escritorio.
5. En Inventario, selecciona un producto → Historial → Traslados. Solo lo verán operadores con `Warehouse.View`.

## Compatibilidad y firma

- ID: `warehouses`; versión: `1.3.3`.
- Requiere POS Desktop API 1.1.0.
- No requiere migraciones: mantiene `warehouses:0001`.
- Firmante: `pos-warehouses-dev-2026`.
- SHA-256 del módulo: `FAAADDCA4BE1E87DC0687CD2F9E485C5242DFD11857206A55579C6D717C2DD28`.
- SHA-256 del POS anfitrión: `58B6D644B76D9A236771D9163B0A9A9251C7382AA6E646074C6ED4F0AE144765`.
- `publisher-public.pem` es la clave pública para verificar el paquete; no contiene clave privada.

Verifica la firma desde la raíz del repositorio:

```powershell
.\scripts\Test-SignedModulePackage.ps1 `
  -PackagePath .\modulos\warehouses\1.3.3\warehouses-1.3.3.zip `
  -PublicKeyPath .\modulos\warehouses\1.3.3\publisher-public.pem
```

Consulta [Hito 19](../../../docs/specs/hito-19-inventario-historial-y-diseno.md) y la [guía de módulos](../../../docs/manuales/crear-modulos-pos.md).
