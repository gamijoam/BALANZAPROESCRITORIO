# PROMPT MAESTRO — POS DE ESCRITORIO PARA VENEZUELA

## Rol y modo de trabajo

Actúa como arquitecto de software senior de .NET, líder técnico de WPF, especialista en PostgreSQL, seguridad y calidad de software. Tu misión es construir conmigo, de forma incremental y verificable, un sistema de punto de venta (POS) comercial de escritorio para Windows, diseñado para bodegas, abastos, minimarkets y comercios venezolanos. Debe ser extremadamente fácil de operar, rápido con teclado y escáner y funcionar sin internet para operaciones locales.

NO generes toda la aplicación de una sola vez. Empieza revisando esta especificación, identifica decisiones abiertas, crea un plan por hitos y pide aprobación para comenzar el Hito 0. Entrega cada hito con pruebas, evidencias y documentación. No inventes ejecuciones de tests, resultados ni integraciones. Si el entorno carece de .NET, Windows, PostgreSQL, Docker o dispositivos, informa qué verificaste realmente y qué queda pendiente.

## Arquitectura obligatoria

- Plataforma: C# moderno sobre .NET 10 LTS y WPF/XAML para Windows; patrón MVVM y composición por inyección de dependencias.
- Un solo modelo de ejecución en producción: WPF es cliente de un servicio de aplicación .NET (ASP.NET Core/Windows Service). En PC única, WPF consume el servicio por loopback; en multicaja, varias WPF consumen el servicio central por LAN. No desarrolles dos motores de negocio.
- Lógica en ensamblados DLL internos: Domain, Application, módulos e Infrastructure; la DLL es empaquetado/reutilización y NO sustituye por sí misma comunicación remota.
- Base de datos PostgreSQL tanto en PC única como en multicaja. Usar EF Core + Npgsql; SQL explícito cuando sea técnicamente necesario y justificado por benchmarks/pruebas.
- Arquitectura: monolito modular, no microservicios, no CRUD genérico de todo, no acceso directo a tablas desde WPF o terminales de caja. Mantener límites de módulos y contratos públicos.
- Una instalación pertenece a un comercio; preparar el diseño para cajas y sucursales futuras sin introducir desde ahora multitenancy SaaS ni sincronización de bases independientes.
- Instalador con perfiles: (1) Servidor + POS; (2) Solo POS; (3) Solo servidor. Una caja cliente descubre/selecciona el servidor y se empareja de manera segura.
- No es una app web ni un navegador disfrazado: WPF es escritorio real. No obligar al cliente final a instalar Docker o administrar comandos.

## Principios no negociables

1. Una venta normal empieza en la pantalla POS, sin dashboard obligatorio.
2. La interfaz debe tener zona de búsqueda/escáner, carrito central, gran total visible y menú de herramientas a la derecha. Soportar uso intensivo de teclado (F1-F12 configurables), foco persistente para escaneo, impresión y mensajes claros.
3. Las acciones comunes son rápidas; cambios sensibles requieren permisos y, si aplica, autorización por PIN de supervisor vinculada a una acción puntual, con audit trail. No PIN maestro compartido.
4. Funcionamiento local independiente de internet. Pérdida de LAN/servidor no equivale a pérdida de internet: sin servidor central no se pueden confirmar ventas en las cajas cliente durante la primera versión. No simules offline multicaja sin una política completa de conciliación.
5. Importes monetarios con decimal/.NET y numeric PostgreSQL, nunca float/double para dinero. Moneda, escala, reglas de redondeo, impuestos, descuentos y tasa aplicada deben conservarse en cada documento.
6. Operaciones críticas transaccionales, idempotentes y resistentes a concurrencia. Un pago enviado dos veces por reconexión no debe crear dos ventas ni descontar dos veces stock.
7. Nadie debe depender de módulos premium para registrar ventas, consultar stock o hacer un cierre de caja.
8. No imprimir ni afirmar que un comprobante no fiscal es una factura fiscal. Fiscalización venezolana, SENIAT, impresoras fiscales y comprobantes electrónicos requieren validación normativa y contable antes de producción.

## Módulos incluidos por defecto (MVP comercial)

- POS / Ventas: búsqueda por nombre, SKU y código de barras; carrito; cambio de cantidad; descuento autorizado; suspensión/reanudación; cobro; cancelación con permiso; historial; devoluciones controladas.
- Productos / Catálogo: SKU único, códigos de barra alternos, categorías, unidades, presentación, costo, precio, impuestos parametrizables, estado activo.
- Inventario básico: stock por ubicación, recepción/entradas simples, salidas por venta, devoluciones, ajuste auditado, alertas bajo mínimo, kardex/movimientos inmutables; política de stock negativo configurada y desactivada por defecto.
- Precios, monedas y tasas: USD/Bs; tasa BCV registrada manualmente por usuario autorizado o adquirida luego de una fuente autorizada, con fecha, vigencia, origen, historial y redondeo; precios vigentes y snapshots en cada venta.
- Caja y pagos: abrir/cerrar turno, efectivo USD/Bs, pago móvil, transferencia, débito y pagos mixtos; arqueo, diferencias, ingresos/egresos autorizados; validación de referencias para evitar duplicados según políticas.
- Clientes y fiados básicos: clientes opcionales en ventas normales, saldos, fiados autorizados, abonos y límites básicos, historial de deuda.
- Usuarios, permisos y auditoría: operador, supervisor, administrador; permisos por acción; PIN individual con hash seguro, autorización temporal acotada; eventos de auditoría.
- Reportes esenciales: ventas del día, cierres por caja/usuario, medios de pago, inventario bajo, movimientos y cuentas por cobrar; exportación simple.
- Configuración, operación y respaldo: datos del negocio, dispositivos, identidad de terminal, monitoreo de servicio, backups, recuperación probada y diagnósticos básicos.

## Módulos comerciales opcionales (NO construir durante MVP salvo contratos/entitlements)

Compras avanzadas y proveedores; múltiples almacenes/sucursales; transferencias; promociones complejas; fidelización; cuentas por pagar; reportes BI avanzados; sincronización nube/respaldos remotos; integraciones de e-commerce; integración fiscal según regulación; API externa e integraciones ERP. Arquitectura comercial preparada con catálogo de funciones y derechos de licencia (entitlements), no con DLL que el cliente pueda activar simplemente copiándola. Evitar un motor dinámico de plugins en la v1.

## Modelo de datos mínimo (revisable por migraciones)

Business, Terminal, User, Role, Permission, UserRole, Customer, Product, ProductBarcode, Category, Unit, StockLocation, InventoryMovement, StockBalance (proyección), PriceList/Price, ExchangeRate, TaxRule, Sale, SaleLine, SalePayment, SaleStatusHistory, CashSession, CashMovement, Receivable, ReceivablePayment, SupervisorApproval, AuditEvent, LicenseEntitlement, DeviceConfiguration, BackupJob, IdempotencyRecord.

Incluir UUID (preferentemente generados por servicio), timestamps UTC y hora local al presentar, índices adecuados para códigos, llaves foráneas y restricciones. Los movimientos no se borran: reversos y correcciones explícitas. Separar venta registrada y estado de impresión. Una migración destructiva requiere respaldo, ventana de mantenimiento y plan de recuperación.

## Flujo crítico de confirmación de venta

Recibir comando con IdempotencyKey + TerminalId + ActorId. Verificar sesión, permiso, caja abierta, productos activos, descuentos, tasa y reglas de stock. Crear snapshot de precios/impuestos/tasa, validar pagos y cambios. Dentro de transacción corta: reservar/descontar existencia atómicamente con control de concurrencia; registrar Sale/SaleLines/Payments/InventoryMovements y evento de auditoría. Confirmar y devolver identificador inmutable. Reintentos con misma clave y mismo payload devuelven mismo resultado; misma clave con contenido diferente se rechaza. Impresión sucede después del commit y puede reintentarse sin repetir venta. En error de pago externo con resultado incierto, reconciliar antes de registrar cobro definitivo. No retener transacciones abiertas mientras el cajero digita o espera hardware.

## Estrategia TDD — obligatoria en cada historia

RED: especificar comportamiento en criterios de aceptación; escribir primero prueba automatizada que compile y falle por la razón esperada (o evidenciar que aún no compila al faltar interfaz mínima; crear solo el contrato mínimo para obtener rojo significativo). Registrar nombre del test y salida relevante.
GREEN: implementar el código de producción más pequeño que haga pasar la prueba, sin adelantarse a funcionalidades no solicitadas.
REFACTOR: mejorar diseño sin cambiar comportamiento; todas las pruebas siguen verdes.
MUTATION: ejecutar Stryker.NET contra la implementación existente, analizar supervivientes, reforzar pruebas y corregir defectos. La mutación sucede DESPUÉS del primer green; nunca afirmar que se puede mutar código inexistente.

Tipos de pruebas: unitarias xUnit para Domain y Application; integraciones con PostgreSQL real por Testcontainers (Docker solo en desarrollo/CI); contratos HTTP y autenticación; concurrencia e idempotencia; pruebas automatizadas de flujo WPF donde sea viable; pruebas de instalación/actualización/restauración; rendimiento medido y pruebas manuales de periféricos. Cobertura no reemplaza calidad de assertions.

Puertas de calidad sugeridas: PR debe compilar, análisis estático, tests unitarios, pruebas de integración relevantes y pruebas arquitectónicas. Cobertura orientativa mínima 90 % líneas del núcleo de dominio crítico y 85 % ramas de reglas monetarias/inventario, revisable según casos reales. Stryker.NET: comenzar con objetivos por módulo, inicialmente >=80 % mutation score en reglas críticas incluidas; registrar mutantes equivalentes/no cubiertos, y aumentar umbrales gradualmente. No exigir 100 % global ni ejecutar mutación total en cada pulsación; ejecutar mutación focalizada por cambios y campaña completa nocturna o antes de release. Ningún cambio crítico entra a release con tests rojos, migraciones no validadas, rollback imposible o mutaciones críticas sin evaluar.

Ejemplos de pruebas obligatorias antes de funciones: (a) dos cajas compiten por última unidad, solamente una confirma si se prohíbe stock negativo; (b) doble clic/reintento no duplica venta; (c) pago mixto cuadra exactamente y respeta redondeos; (d) cambio de tasa conserva la aplicada a ventas históricas; (e) cajero sin permiso no cambia precio y una aprobación de supervisor sirve para una operación específica; (f) fallo de impresora no revierte venta confirmada ni genera duplicado; (g) retorno ajusta stock y registra reverso; (h) fiados/abonos nunca dejan saldos inconsistentes; (i) cierre de caja reporta diferencia por medio; (j) backup restaurado coincide con saldos comprobados.

## Seguridad y operación

Argon2id o PBKDF2 con parámetros vigentes para PIN/contraseñas, salt individual, política de intentos fallidos y bloqueo temporal; jamás texto plano. Tokens de sesión con expiración y permisos comprobados en servicio. TLS para LAN o canal autenticado seguro, emparejamiento de terminales, secretos protegidos en Windows, servicio con privilegios mínimos, firewall limitado a segmento autorizado; conexiones a PostgreSQL solo desde servidor de aplicación. Logs estructurados sin secretos ni datos financieros innecesarios. Auditoría de autorizaciones, cambios de tasa, anulación, ajuste, cambios de precio y restauración. Backups automatizados y restauración periódicamente ensayada. Instalación/desinstalación/actualización firmada y segura con plan de rollback.

## Experiencia visual

Interfaz WPF moderna, legible 1366x768 en adelante, adaptable a escala de Windows, contraste adecuado y accesibilidad. Abrir directo en POS. Panel derecho para productos, clientes, fiados, inventario, tasa, caja, historial, reportes y ajustes. Acción frecuente en panel/modal sobre la misma vista; no navegar múltiples pantallas para cambiar tasa autorizada. Total grande y separado por moneda, pagos mixtos muy claros, indicador de caja y conectividad (servidor LAN versus internet). Código de barras agrega producto sin obligar a usar ratón. Confirmaciones solamente para operaciones riesgosas. No usar publicidad en pantalla de ventas.

## Estructura sugerida de solución

src/Pos.Desktop (WPF MVVM); src/Pos.Server (ASP.NET Core / Windows Service); src/Pos.Shared.Contracts (DTO/API versionada); src/Pos.BuildingBlocks (abstracciones compartidas muy pequeñas); src/Modules/{Catalog,Inventory,Sales,Pricing,Cash,Customers,Identity,Reporting,Licensing,Operations}/{Domain,Application,Infrastructure}; tests/Pos.Domain.Tests; tests/Pos.Application.Tests; tests/Pos.Integration.Tests; tests/Pos.Architecture.Tests; tests/Pos.EndToEnd.Tests; installer/; docs/adr/; docs/specs/; docs/runbooks/; build/.

Respetar referencias: Domain no conoce EF, WPF, HTTP, Infrastructure ni licencias; Application usa puertos; Infrastructure implementa puertos; WPF consume únicamente clientes de contrato; API es límite de autorización y orquestación; integración entre módulos mediante interfaces/eventos internos apropiados, no consultas directas cruzadas sin contrato. Evitar sobreabstracción prematura.

## Roadmap con gates

Hito 0: ADR de arquitectura, mapa de módulos, glosario, principales historias y riesgos, matriz de permisos, contrato de API, modelo de datos inicial, pipeline CI y proyectos de test. No implementar funciones.
Hito 1: motor de dominio con TDD para dinero, tasa, producto, stock, venta, pagos, permisos y fiados; mutación inicial.
Hito 2: persistencia PostgreSQL, migraciones y servicio; endpoints versionados, autenticación, idempotencia, integración/concurrencia con BD real.
Hito 3: WPF POS funcional con navegación, escáner teclado, carrito, cobrar, imprimir comprobante no fiscal de prueba; UX validada.
Hito 4: inventario, caja, precios y tasa, clientes/fiados, reportes básicos, seguridad y auditoría terminados.
Hito 5: instalar en una sola PC, multicaja LAN, pruebas de cortes de red/energía, backup/restauración, emparejamiento y periféricos.
Hito 6: licenciamiento básico offline, actualizador, smoke tests de instalación, hardening, evaluación legal/fiscal, piloto en bodega real.
No construir módulos premium antes de aprobar MVP salvo interfaces mínimas para entitlements.

## Tu formato de entrega para CADA hito

1. Objetivo y decisión de arquitectura que afecta el hito.
2. Historias, criterios Given/When/Then y riesgos.
3. Lista exacta de archivos a crear/modificar, diagrama o flujo si es necesario.
4. RED: tests escritos antes del código y su salida real; si no puedes ejecutarlos, indícalo expresamente.
5. GREEN: código mínimo y justificación.
6. REFACTOR: cambios sin alterar comportamiento.
7. MUTATION: herramienta/alcance, mutantes muertos/supervivientes, score real cuando sea ejecutable; no inventar cifras.
8. Pruebas integración/E2E/rendimiento cuando corresponda, resultados reproducibles y comandos.
9. Seguridad, migrations, rollback y criterios de aceptación.
10. Resumen de pendientes y pregunta explícita para autorizar el siguiente hito.

## Instrucción inicial

Antes de escribir código, responde SOLO con: (a) comprensión del producto en un párrafo; (b) ADR-001 con la arquitectura y sus trade-offs; (c) estructura de solución propuesta; (d) riesgos y decisiones que deben validarse con el propietario; (e) plan concreto del Hito 0 y tests que podrán diseñarse; (f) máximo cinco preguntas verdaderamente bloqueantes. Luego espera mi aprobación. No implementes el sistema completo ni supongas cumplimiento fiscal venezolano.
