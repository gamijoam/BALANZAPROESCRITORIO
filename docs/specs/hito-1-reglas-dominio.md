# Hito 1 Reglas iniciales del dominio

## Alcance

Se creó `Pos.Domain` como biblioteca independiente de WPF, ASP.NET Core, EF Core y PostgreSQL. Sus reglas se prueban en `Pos.Domain.Tests`. La implementación es lógica de dominio en memoria; todavía no persiste movimientos, confirma ventas en una transacción real, autentica operadores ni garantiza concurrencia entre cajas.

## Reglas cubiertas

### Dinero y tasa

- `Money` almacena `decimal` y moneda `USD` o `VES`. Permite valores con signo para poder representar diferencias/reversos; los agregados comerciales validan cuando el valor debe ser no negativo o positivo.
- Suma y resta requieren la misma moneda. No hay redondeo implícito en aritmética; el redondeo recibe escala y modo explícitos. El modo por defecto es `AwayFromZero`.
- No se fijó una escala universal para VES ni USD. Cada operación recibe la escala definida por la regla comercial, pendiente de confirmar para producción.
- `ExchangeRate` identifica moneda origen y destino, requiere tasa mayor que cero, origen no vacío y vigencia en UTC. La conversión multiplica por tasa y redondea a la escala solicitada. Cada venta deberá persistir el snapshot, la fuente, la hora y la tasa efectivamente aplicada.

### Producto, stock y descuentos

- El producto requiere SKU y nombre; el precio no puede ser negativo y mantiene la moneda estable en un cambio directo de precio. Los códigos de barra se recortan, se distinguen sin importar mayúsculas/minúsculas y no se exponen como una colección mutable.
- El balance inicial de stock no puede ser negativo. Recepción y salida requieren cantidad positiva. La política inicial rechaza vender más de lo disponible y deja el balance sin cambios ante una salida inválida.
- El porcentaje de descuento y el máximo permitido están entre 0 y 100. Un porcentaje sobre el máximo requiere que la capa Application haya verificado una autorización de supervisor ligada a la operación.
- Una línea guarda una copia de SKU, descripción, cantidad, precio, tasa de impuesto, descuento y totales. Cálculo provisional: precio × cantidad, aplicar descuento, redondear subtotal, calcular impuesto sobre el subtotal descontado y redondear impuesto, luego sumar. La tasa de impuesto se expresa como fracción entre 0 y 1. Esto no define la regla fiscal venezolana; requiere validación contable/legal.

### Venta y pagos

- Una venta empieza como borrador, necesita líneas de una sola moneda y se confirma una vez, con pagos suficientes para cubrir su total.
- Un pago puede expresarse en moneda distinta a la venta si incluye una tasa con dirección correcta (moneda del pago → moneda de la venta). La suma liquidada se calcula y redondea a la escala configurada.
- Solo una entrega en efectivo, única y en moneda de la venta, puede producir vuelto. Se rechaza el sobrepago de medios no efectivo o de pagos mixtos hasta que el negocio defina una política segura de vuelto.
- Las referencias externas presentes en una venta deben ser únicas sin importar mayúsculas/minúsculas. La deduplicación entre ventas y reintentos dependerá de restricciones persistentes y políticas por medio de pago.
- Una venta de total cero puede confirmarse sin una entrega monetaria.
- La clave de idempotencia, la huella del payload, la transacción PostgreSQL, el control de stock concurrente y la trazabilidad de impresión aún no están implementados; pertenecen a Application/Infrastructure y al Hito 2.

### Permisos, autorización y fiados

- `PermissionSet` valida que los permisos existan y concede únicamente los enumerados.
- Una autorización de supervisor se vincula a un permiso, una persona, una operación y una expiración UTC. Se consume una sola vez de forma atómica dentro del proceso. Esto no sustituye autenticación, hash de PIN, almacenamiento duradero ni auditoría.
- La cuenta por cobrar valida cliente, moneda, límite de crédito, cargos y abonos positivos. No permite superar el límite, abonar más que el saldo ni dejar un saldo negativo. El libro inmutable de movimientos se implementará en persistencia.

## TDD y evidencia

### RED

Primero se escribieron pruebas contra `Pos.Domain` sin namespaces ni tipos de dominio. El comando `dotnet test .\tests\Pos.Domain.Tests\Pos.Domain.Tests.csproj --no-restore --configuration Debug` falló por la razón esperada: errores `CS0234` al faltar `Money`, `Catalog`, `Inventory`, `Sales`, `Identity` y `Customers`.

Después se añadieron tres pruebas de regresión para proteger la colección de códigos de barra, rechazar valores de permiso desconocidos y permitir una liquidación de total cero. La ejecución filtrada falló en las tres con el comportamiento esperado antes de corregir el código.

### GREEN

`dotnet test .\tests\Pos.Domain.Tests\Pos.Domain.Tests.csproj --configuration Debug` terminó con **50 superadas, 0 fallidas, 0 omitidas**. Los casos incluyen aritmética y conversión, redondeo, producto/códigos, stock, descuentos/impuestos, total de venta, pagos mixtos, vuelto, referencias, permisos, autorización concurrente y cuenta por cobrar.

### REFACTOR

Se separaron tipos por namespaces y responsabilidades, se encapsuló la colección de códigos y se hizo atómica la consumición de autorización. `dotnet format .\Pos.sln --no-restore` aplicó el formato de la solución; la comprobación inicial había señalado solo el `AssemblyInfo.cs` generado por WPF.

### MUTATION

Se añadió `dotnet-stryker` como herramienta local fijada en `dotnet-tools.json` (versión 5.0.0). Ejecución desde `tests/Pos.Domain.Tests` el 8 de octubre de 2026:

- 366 mutantes encontrados.
- 313 evaluados: 263 muertos y 50 supervivientes.
- 19 con error de compilación, 32 ignorados por filtro de bloque y 2 sin cobertura.
- Mutation score informado por Stryker: **83.49 %**.
- Sin timeouts ni errores de ejecución.

Se reforzaron límites y reglas críticas después de los informes iniciales, elevando el score desde 61.89 % y luego 66.45 %. Los supervivientes finales incluyen cambios de textos de error y mutaciones de ramas redundantes/invariantes o cuya condición se vuelve a validar por otra regla. Se deben reevaluar cuando cambien estos contratos; el score no prueba cumplimiento fiscal, consistencia de base de datos ni seguridad integral.

## No cubierto y gate

- No se ha probado persistencia PostgreSQL, concurrencia entre procesos/cajas, idempotencia HTTP, reintentos de pago externo, devoluciones persistidas, auditoría, ni restauración de respaldos.
- No se ha validado escala/rounding fiscal, IVA aplicable, comprobantes, tasa BCV ni política de cambio con un asesor del negocio.
- Antes del siguiente hito, confirmar moneda de referencia/precios, escalas/redondeo, regla de vuelto en pagos mixtos y quién puede autorizar descuentos, ajustes y reversos.
