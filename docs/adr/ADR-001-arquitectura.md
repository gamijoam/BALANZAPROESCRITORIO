# ADR-001 Arquitectura de ejecución

- **Estado:** propuesta aceptada para el Hito 0; contratos y decisiones de detalle pendientes.
- **Fecha:** 8 de octubre de 2026.
- **Contexto:** el POS debe operar en una PC sin internet y crecer a varias cajas conectadas por LAN, conservando un solo motor de negocio y una base de datos central.

## Decisión

Usar C# sobre .NET 10 LTS, WPF/XAML con MVVM, y un servicio de aplicación ASP.NET Core ejecutado como Windows Service. La interfaz WPF se comunica con el servicio por loopback en una instalación de una PC y por LAN en instalaciones multicaja. El servicio usa PostgreSQL mediante EF Core y Npgsql. El sistema se organiza como monolito modular con capas Domain, Application e Infrastructure y contratos HTTP versionados.

La aplicación WPF solo conoce DTOs/contratos del servicio y no abre conexiones PostgreSQL. El servicio centraliza autenticación, permisos, reglas de negocio, transacciones, idempotencia y auditoría. Las reglas de dominio no dependen de WPF, EF Core, ASP.NET Core ni la infraestructura.

## Consecuencias y trade-offs

- Una PC puede vender sin internet mientras el servicio local y PostgreSQL estén disponibles.
- Una caja cliente no puede confirmar ventas si pierde conectividad con el servidor; la v1 no simulará ventas offline multicaja.
- Servicio y base de datos agregan componentes operativos a una instalación de una PC, pero evitan mantener dos motores de negocio.
- PostgreSQL es obligatorio en todas las modalidades; Docker se reserva para desarrollo/CI y no se instala al usuario final.
- La integración entre módulos usa contratos internos/eventos apropiados. No se introducen microservicios ni un cargador dinámico de plugins en la v1.
- Las DLL son empaquetado de bibliotecas .NET; no reemplazan la comunicación por red entre cajas.
- Cambiar el proveedor de datos o incorporar SQL explícito requiere una razón medible y una decisión registrada.

## Despliegue

```text
PC única:  WPF → HTTP loopback → servicio .NET → PostgreSQL local
Multicaja: WPF caja 1 ─┐
           WPF caja 2 ─┼→ HTTP/LAN → servicio .NET central → PostgreSQL central
           WPF caja n ─┘
```

## Decisiones de seguridad asociadas

El servicio es la frontera de autorización. Secretos protegidos por Windows; acceso a PostgreSQL únicamente desde el servicio; sesiones con expiración; emparejamiento de terminales; canal LAN autenticado y protegido; auditoría separada de los logs técnicos. PINs y contraseñas se almacenan con hash adaptativo y salt individual. La elección de algoritmo y parámetros se valida antes de implementar autenticación.

## Pendientes

Confirmar versiones/parches al iniciar cada entrega; mecanismo TLS y certificados LAN; autenticación y emparejamiento; proveedor de instalador; respaldo/restauración y política de actualización; topología de PostgreSQL; política de licencias offline.
