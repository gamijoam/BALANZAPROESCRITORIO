# Especificación funcional: módulo financiero y cambios del core

**Fecha:** 14 de octubre de 2026
**Estado:** decisiones de negocio aprobadas para implementación; detalles técnicos restantes se cerrarán por fase.
**Alcance:** requisitos y criterios de aceptación. Este documento no autoriza por sí solo a implementar historias ni modificar datos de comercios.

## 1. Propósito

Definir cómo evolucionará el POS para manejar varios niveles de precio, impuestos por producto, costo histórico de ventas y operaciones financieras de proveedores. La solución separa las reglas indispensables para vender del módulo opcional que administra compras y finanzas.

La interfaz WPF continuará consumiendo contratos HTTP. El servicio será responsable de permisos, cálculos, integridad, idempotencia y auditoría. Ningún módulo de escritorio accederá directamente a PostgreSQL.

## 2. Separación entre core y módulo financiero

| Capacidad | Ubicación | Motivo |
|---|---|---|
| Precio 1, Precio 2 y Precio 3 por producto | Core: catálogo y ventas | El servidor debe validar el precio elegido en cualquier venta, con o sin módulo financiero instalado. |
| Perfil de impuesto por producto y cálculo por línea | Core: catálogo y ventas | El impuesto afecta el total confirmado y debe aplicarse siempre según la configuración del artículo. |
| Permisos para elegir nivel, precio especial y aprobación puntual | Core: identidad y ventas | El servicio debe rechazar precios no autorizados aunque el cliente WPF esté modificado o desactualizado. |
| Snapshot de precio, nivel elegido, moneda, tasa e impuesto | Core: venta | Una venta confirmada conserva los valores usados aunque el producto o sus reglas cambien después. |
| Campo de costo actual y snapshot del costo en la venta | Core: catálogo y ventas | Permite preservar el costo conocido al confirmar. Si todavía no se conoce, el snapshot puede ser nulo. |
| Proveedores, facturas de compra, recepción y cuentas por pagar | Módulo instalable finance | Son funciones comerciales opcionales y no deben ser requisito para vender o cerrar caja. |
| Cálculo y actualización del costo promedio | Servicio financiero del módulo finance, mediante contrato interno aprobado | El módulo procesa compras; no escribe directamente en tablas de otros módulos. |
| Abonos a proveedores, cuentas de tesorería y gastos | Módulo finance, por fases | Amplían compras/CxP y requieren movimientos financieros propios, auditables e inmutables. |
| Reportes de utilidad, costo de inventario y resultados preliminares | Endpoints del servicio y fuentes compatibles con advanced-reports | Los reportes consultan datos del servicio; no leen tablas de módulos directamente. |

El core debe permitir vender aunque finance no esté instalado. En ese caso el costo puede ser desconocido y los reportes de utilidad deben indicarlo, nunca asumir costo cero.

## 3. Decisiones de negocio aceptadas

### 3.1 Precios

- Precio 1 será el precio actual del producto y conservará su moneda configurada.
- Precio 2 y Precio 3 se guardarán en la misma moneda base del producto.
- El cajero podrá usar Precio 1; el uso de Precio 2 dependerá de los permisos configurados. Precio 3 requerirá aprobación puntual de supervisor.
- Un precio manual distinto de los niveles publicados requerirá motivo, permiso y auditoría.
- Al confirmar una venta se guardarán el nivel elegido y el precio efectivamente aplicado.
- El margen bruto objetivo se interpreta como (precio menos costo) dividido entre precio. El precio sugerido para margen m es costo dividido entre (1 menos m).
- Una compra podrá mostrar propuestas de actualización de Precio 1/2/3, pero nunca cambiará el catálogo sin confirmación explícita de un operador autorizado. Cada cambio confirmado genera historial.

### 3.2 Impuestos

- Los precios del catálogo no incluyen impuesto; el servicio calcula el impuesto por línea y lo suma al total.
- Cada producto tiene una regla propia. El tratamiento de un producto no cambia el de los demás.
- Tratamientos iniciales propuestos: gravado 16 %, gravado 8 %, gravado 0 % y exento. Exento se conserva separado de gravado 0 %.
- La venta congela tratamiento, tasa e importe de impuesto aplicados.
- En una venta con varios tratamientos, el total es la suma de las líneas; el reporte conserva subtotales por tratamiento y tasa.
- No se afirma cumplimiento fiscal por modelar estos tratamientos. Numeración, cálculo o comprobantes fiscales requieren definición y validación aparte.

### 3.3 Tasas y moneda

- Una persona con permiso registra la tasa, la fuente y sus fechas de vigencia.
- Al vencer la tasa, el servidor bloquea operaciones que necesiten conversión hasta que exista una tasa vigente. Las operaciones que no necesiten conversión pueden continuar.
- La tasa, moneda de origen y conversión aplicada quedan congeladas en ventas, facturas y pagos.
- La deuda de una factura mantiene la moneda original de esa factura. Un abono en otra moneda registra el importe recibido, su tasa y su diferencia cambiaria sin reescribir el saldo original.

### 3.4 Costo e historia

- La valoración de existencia usará costo promedio ponderado, sujeto a las definiciones técnicas pendientes de la sección 7.
- El costo aplicado se guarda en cada nueva línea de venta al confirmar. Después no se recalcula por cambios de costo.
- Las ventas antiguas sin snapshot de costo se reportan como costo no disponible; no se completan con costo cero ni con una estimación presentada como real.
- La moneda de la factura, la tasa utilizada y los cargos incluidos en el costo deben conservarse para explicar el cálculo.

### 3.5 Finanzas y reportes

- La primera fase de finance cubre proveedores, facturas/recepciones, cuentas por pagar y pagos parciales.
- Tesorería y gastos operativos quedan para una fase posterior. Nómina no forma parte de la primera fase.
- Métodos de pago y cuentas receptoras son conceptos separados. Por ejemplo, Zelle puede ser un método de pago vinculado a una cuenta USD.
- El cierre de caja concilia importes; no crea automáticamente un segundo ingreso en tesorería. El traspaso a una cuenta requiere una acción explícita.
- Los movimientos financieros y de inventario se corrigen mediante reversos, no editando o eliminando el movimiento original.
- La utilidad bruta usa ingresos netos de descuentos e impuestos menos el costo congelado en venta, con ajustes por devoluciones.
- La valoración de inventario tendrá fecha de corte y método identificable.
- El estado de resultados se etiqueta como preliminar y no como contabilidad formal.

## 4. Requisitos funcionales

### Catálogo, precios e impuestos — core

- **CORE-01:** El producto tendrá Precio 1, Precio 2, Precio 3, moneda base y tratamiento de impuesto.
- **CORE-02:** Una migración conserva el precio y moneda existentes como Precio 1. No copia ese precio automáticamente a Precio 2 o Precio 3.
- **CORE-03:** Si un precio alternativo no está configurado, no se ofrece para seleccionar en el carrito.
- **CORE-04:** El servidor calcula el total desde el producto y nivel permitidos; no confía en el total ni en la tasa enviados por WPF.
- **CORE-05:** El perfil de impuesto individual ofrece los tratamientos acordados. Para productos nuevos se puede definir un valor predeterminado configurable; cada artículo puede sobrescribirlo.
- **CORE-06:** El servidor calcula impuesto por línea antes de confirmar. Guarda la tasa y el importe de la línea en el snapshot de venta.
- **CORE-07:** La aprobación de supervisor está ligada a operador, terminal, permiso y operación exacta; vence y se consume una sola vez.
- **CORE-08:** Cambios de precios, tratamientos fiscales, costos y márgenes objetivo quedan auditados con actor, fecha, valores anterior/nuevo y motivo cuando corresponda.

### Compras y costo — módulo finance

- **FIN-01:** Una factura de proveedor se registra como borrador antes de afectar inventario o saldos.
- **FIN-02:** La contabilización de una factura y la recepción asociada son idempotentes. Un reintento no repite stock ni deuda.
- **FIN-03:** Se rechaza una factura duplicada según una clave de negocio definida con proveedor y número de documento/control.
- **FIN-04:** Una factura a crédito crea la obligación por el importe y moneda originales. Una factura pagada registra el pago relacionado.
- **FIN-05:** La recepción actualiza existencias y costo promedio a través de servicios/contratos del servidor, coordinando la transacción con inventario.
- **FIN-06:** El costo unitario incluye únicamente los componentes que el propietario defina como atribuibles al inventario. El cálculo muestra moneda, tasa, flete/descuentos y resultado antes de confirmar.
- **FIN-07:** Los nuevos precios sugeridos se muestran para revisión. No se publican hasta una confirmación autorizada y su correspondiente historial.
- **FIN-08:** Una recepción, factura, pago o corrección mantiene actor, fecha, referencia y auditoría; la corrección genera reverso.

### Cuentas por pagar y tesorería — módulo finance

- **FIN-09:** Se permiten abonos parciales. Cada pago conserva medio, cuenta receptora, moneda, importe, referencia y tasa aplicable.
- **FIN-10:** Una deuda en USD pagada parcialmente en VES mantiene el principal en USD y registra aparte la conversión y diferencia realizada.
- **FIN-11:** Tesorería distingue el medio por el que se recibió el dinero de la cuenta donde se mantiene.
- **FIN-12:** Cerrar caja no genera por sí solo un movimiento de tesorería. Un traslado explícito debe referenciar el cierre/origen para impedir doble conteo.

### Reportes

- **REP-01:** El reporte de utilidad por venta y producto usa snapshots de costo y precio; muestra costo no disponible cuando falta información histórica.
- **REP-02:** El reporte de inventario valorizado indica método de costo, fecha de corte, moneda de presentación y alcance de almacenes.
- **REP-03:** El estado de resultados se identifica como preliminar y expone los componentes usados para su cálculo.
- **REP-04:** Los reportes respetan permisos del servidor y consultan datos mediante contratos; no acceden directamente a PostgreSQL desde WPF.

## 5. Criterios de aceptación y pruebas candidatas

Las siguientes pruebas se escribirán primero y deberán fallar por el comportamiento esperado antes de implementar cada historia. Los nombres son propuestas para el conjunto de pruebas; no son pruebas ya creadas o ejecutadas.

| ID | Criterio de aceptación | Prueba candidata |
|---|---|---|
| AC-01 | Dado un producto existente, cuando se migra el catálogo, entonces su precio actual permanece como Precio 1 y Precio 2/3 quedan sin configurar. | ProductPriceMigrationPreservesPrice1AndLeavesOtherTiersUnset |
| AC-02 | Dado un cajero sin permiso para Precio 2, cuando envía una venta usando ese nivel, entonces el servicio la rechaza y no registra venta ni stock. | CheckoutRejectsUnauthorizedPriceTier |
| AC-03 | Dada una autorización para Precio 3, cuando se reutiliza o cambia el producto/operación, entonces se rechaza la segunda solicitud. | SupervisorApprovalIsSingleUseAndBoundToPriceSelection |
| AC-04 | Dado un carrito con artículos gravados a 16 % y sin impuesto, cuando se confirma, entonces el impuesto se calcula solo según el tratamiento de cada producto. | CheckoutCalculatesTaxPerProductTreatment |
| AC-05 | Dada una venta confirmada, cuando cambia después el precio, costo o tasa de un producto, entonces el comprobante histórico conserva sus snapshots. | SaleSnapshotsPriceCostTaxAndRate |
| AC-06 | Dada una tasa vencida, cuando una línea o pago necesita conversión, entonces el servicio rechaza la operación sin crear registros parciales. | CheckoutRejectsExpiredExchangeRateForConversion |
| AC-07 | Dada una factura repetida o un reintento de contabilización, cuando se procesa otra vez, entonces no duplica recepción, deuda ni pago. | PurchasePostingIsIdempotentAndRejectsDuplicateInvoice |
| AC-08 | Dada una factura recibida en otra moneda, cuando se contabiliza, entonces conserva el documento original y actualiza existencias/costo una sola vez según la política aprobada. | PurchaseReceiptUpdatesWeightedAverageCostOnce |
| AC-09 | Dada una deuda y un abono parcial en moneda distinta, cuando se registra el pago, entonces el principal sigue expresado en moneda de factura y la diferencia cambiaria queda separada. | SupplierPaymentPreservesInvoiceCurrencyAndRecordsFxDifference |
| AC-10 | Dado un cierre de caja conciliado, cuando no se hace un traspaso explícito, entonces tesorería no registra un ingreso duplicado. | CashCloseDoesNotDuplicateTreasuryBalance |
| AC-11 | Dada una venta antigua sin costo, cuando se consulta utilidad, entonces se muestra sin costo histórico disponible y no como margen calculado con costo cero. | ProfitReportMarksLegacySaleCostAsUnavailable |
| AC-12 | Dada una actualización con datos existentes, cuando se aplica la migración, entonces los movimientos previos permanecen intactos y existe respaldo y camino de recuperación. | FinanceMigrationPreservesExistingSalesAndInventory |

## 6. Fuera de alcance inicial

- Nómina, contabilidad general de doble partida y presentación tributaria.
- Certificación fiscal, numeración fiscal o cumplimiento normativo inferido por la presencia de tasas.
- Automatización de precios sin revisión humana.
- Estimar costos históricos que no fueron guardados.
- Acceso directo desde el escritorio a tablas PostgreSQL.
- Multitenancy SaaS o sincronización entre bases independientes.

## 7. Definiciones técnicas pendientes antes de implementar

Estas preguntas no revierten las decisiones aceptadas; concretan cómo implementarlas con datos reales.

1. **Clasificación de productos existentes:** aprobado: los artículos existentes se migran como pendientes y no se pueden vender hasta que un administrador asigne su tratamiento fiscal. No aplicar una tasa global silenciosamente.
2. **Taxonomía fiscal del producto:** cerrar si “sin impuesto” es un tratamiento distinto de “exento”. Mantener “gravado 0 %” separado de “exento”, según lo ya acordado.
3. **Moneda del costo promedio:** propuesta para validar: mantener el costo promedio en la moneda base del producto y conservar además moneda/importe/tasa de cada factura.
4. **Costo puesto en inventario:** definir distribución de flete, descuentos, impuestos recuperables/no recuperables, devoluciones y diferencias de unidades/presentaciones.
5. **Ciclo de factura y recepción:** definir borrador, contabilizada, recepción parcial/total, devolución, anulación por reverso y regla exacta para identificar duplicados del proveedor.
6. **Diferencia cambiaria:** definir la tasa que se usará al pagar parcialmente una factura y la fórmula de reconocimiento de la diferencia.
7. **Almacenes:** declarar si finance requiere obligatoriamente warehouses o si una compra puede recibirse en el inventario básico. Fijar versiones mínimas de API y módulo.
8. **Reportes:** cerrar moneda de presentación, zonas horarias, filtros, tratamiento de devoluciones y fecha de corte.
9. **Precisión y redondeo:** fijar escalas monetarias, cantidad, tasa y orden de redondeo con casos de aceptación antes de migrar datos.
10. **Permisos:** agregar a la matriz los permisos de compra, recepción, cambios de costo/precio sugerido, pagos de proveedor, tesorería y exportación financiera.

## 8. Fases propuestas

1. **Core de precios e impuestos:** niveles de precio, tratamientos por artículo, permisos y snapshots de venta. Los productos existentes sin clasificación quedan pendientes y bloqueados para venta hasta que el administrador los clasifique.
2. **Costo y compras:** costo promedio, proveedores, factura/recepción y CxP, con operación idempotente coordinada con inventario.
3. **Pagos y tesorería:** abonos parciales, diferencia cambiaria, cuentas y conciliación sin duplicar saldos.
4. **Reportes financieros:** utilidad bruta, inventario a costo y estado de resultados preliminar.

Cada fase requiere aprobación del hito correspondiente, historias Given/When/Then, pruebas RED antes del código, migración no destructiva, pruebas de integración con PostgreSQL y plan de respaldo/recuperación. La compilación o las pruebas no se consideran ejecutadas hasta tener evidencia real.

El paquete de finance, cuando exista, debe pasar por el flujo de publicación y firma confiable vigente. La clave privada del publicador no se incorpora al repositorio ni al instalador.

## 9. Referencias del proyecto

- [Prompt Maestro y gates de hitos](../../Prompt_Maestro_POS_Escritorio_TDD.md)
- [Diseño verificable Hito 0](hito-0-diseno-verificable.md)
- [Funciones operativas actuales Hito 4](hito-4-funciones-operativas.md)
- [Modelo conceptual de datos](modelo-datos-inicial.md)
- [Matriz inicial de permisos](matriz-permisos.md)
