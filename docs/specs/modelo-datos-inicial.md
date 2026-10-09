# Modelo conceptual inicial de datos

El esquema detallado se definirá por migraciones al comenzar persistencia. Todas las entidades persistentes tendrán UUID y timestamps UTC donde aplique. El modelo debe admitir identificador de negocio e identidad de terminal; no es multitenancy SaaS ni sincronización entre bases en la v1.

## Agregados y entidades candidatas

| Área | Entidades | Invariantes principales |
|---|---|---|
| Comercio/terminal | Business, Terminal, DeviceConfiguration | Una instalación pertenece a un comercio; terminal emparejada e identificable |
| Identidad | User, Role, Permission, UserRole, SupervisorApproval, AuditEvent | PIN/contraseña con hash; autorización de alcance puntual; auditoría con actor y tiempo |
| Catálogo | Product, ProductBarcode, Category, Unit | SKU único; códigos alternos únicos según alcance acordado; productos inactivos no se venden |
| Precios | PriceList/Price, ExchangeRate, TaxRule | Vigencia, moneda, escala y reglas; venta conserva snapshots históricos |
| Inventario | StockLocation, InventoryMovement, StockBalance | Movimiento inmutable; balance es proyección; ajustes/reversiones explícitos |
| Ventas | Sale, SaleLine, SalePayment, SaleStatusHistory, IdempotencyRecord | Operación atómica; montos `numeric`; referencia idempotente y payload verificable |
| Caja | CashSession, CashMovement | Turno por operador/terminal; arqueo por medio; diferencias conservadas |
| Clientes | Customer, Receivable, ReceivablePayment | Saldo por movimientos; abonos no se borran y correcciones son contrapartidas |
| Operación | LicenseEntitlement, BackupJob | Derechos comprobados por servicio; backup con resultado/fecha y restore ensayado |

## Relaciones de negocio

- `Sale` pertenece a una `Terminal`, `User` y `CashSession` y contiene líneas/pagos.
- `SaleLine` conserva descripción/SKU, cantidad, costo/precio aplicados, impuesto, moneda y regla de redondeo necesarios para interpretar el documento histórico.
- Una confirmación de venta genera movimientos de inventario y auditoría correlacionados bajo una sola transacción.
- Una devolución referencia venta y líneas originales; los efectos financieros y de stock son reversos explícitos.
- `IdempotencyRecord` asocia terminal, clave, huella del comando y resultado definitivo; una misma clave no puede aceptar otro payload.
- La caja y los pagos externos conservan referencia/idempotencia según política por validar.

## Restricciones técnicas iniciales

- Dinero y tasa: `decimal` en .NET y `numeric(p,s)` en PostgreSQL con precisión/escala por decidir; nunca `float`/`double`.
- Cantidad: precisión según unidad de producto, todavía por definir.
- Índices para SKU, códigos de barras, fechas/terminal/estado de venta e idempotencia.
- Claves foráneas y restricciones para identidad/referencias; no borrar movimientos financieros ni de inventario.
- Hora almacenada en UTC y presentada en hora local configurada.
- Migración destructiva exige backup, ventana y plan de recuperación.

No se fijan aún tipos numéricos definitivos, estrategia de saldos, bloqueo de concurrencia, partición/esquemas ni retención. Se decidirán con pruebas y requisitos de operación.
