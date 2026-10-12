# Hito 5 — Perfiles de instalación y multicaja

## Objetivo

Permitir que la misma solución se instale como PC independiente, servidor con caja local, servidor sin interfaz de caja o terminal cliente. Todos los modos usan el mismo servicio y modelo de negocio. Las terminales cliente consumen HTTP seguro y nunca reciben credenciales PostgreSQL.

## Criterios de aceptación

- **Dado** el primer inicio, **cuando** no existe perfil de instalación, **entonces** un asistente ofrece: PC independiente (servidor + POS), solo servidor y terminal cliente.
- **Dado** el modo independiente o servidor, **cuando** se configura PostgreSQL, **entonces** la conexión se comprueba antes de guardarse cifrada en el perfil Windows que ejecutará el servicio.
- **Dado** el modo terminal cliente, **cuando** se configura, **entonces** basta con escribir la IP del servidor y confirmar una vez la identidad HTTPS; no se solicitan credenciales de emparejamiento ni PostgreSQL.
- **Dado** dos terminales conectadas con operadores diferentes, **cuando** ambas facturan, **entonces** sus ventas y cajas quedan asociadas a sus respectivos operadores y terminales en la misma base.
- **Dado** una actualización, **cuando** la conexión PostgreSQL protegida está disponible, **entonces** se reutiliza sin rotarla y permanece fuera de la carpeta reemplazada por el paquete.
- **Dado** una actualización sin conexión protegida válida, **cuando** se inicia, **entonces** el actualizador no detiene el servicio ni migra; solicita reconfigurar y validar la conexión primero.
- **Dado** un modo de servidor en LAN, **cuando** se expone el servicio, **entonces** exige TLS, limita el acceso al perfil de red privada y autentica a cada operador con su usuario/PIN normal.
- **Dado** una instalación independiente, **cuando** se instala en otra PC, **entonces** su base local permanece independiente, salvo que se elija explícitamente el modo terminal cliente.

## Decisiones de seguridad

- La contraseña de base nunca se guarda en JSON legible ni se copia al paquete.
- El perfil servidor es el único dueño de la conexión PostgreSQL. La actualización no cambia contraseñas.
- Una terminal cliente conserva un `TerminalId` y la huella SHA-256 del certificado confirmado en su primera conexión; el servidor sigue validando el usuario/PIN del operador por HTTPS.
- No se ofrece venta desconectada en modo cliente durante este hito.

## Historias

1. Asistente de selección de perfil y conexión.
2. Configuración TLS automática y confianza inicial de la terminal cliente.
3. Ventas concurrentes, sesiones de caja y aislamiento por terminal.
4. Actualización que conserva conexión, modo y emparejamientos; recuperación explícita si falta un secreto.

## Alcance por entregar

Este documento autoriza el diseño y la implementación incremental del Hito 5 ampliado. Cada historia tendrá pruebas RED/GREEN y evidencia de instalación antes de habilitarla en un paquete de producción.

## Estado de implementación

- **Completado en esta iteración:** `Configure-LocalDatabase.ps1` prueba y reutiliza la contraseña de `pos_app` cifrada en DPAPI cuando todavía es válida. También intenta conservar la conexión heredada de .NET User Secrets al migrarla. Solo genera una contraseña nueva cuando la guardada no autentica.
- **Completado en código:** el primer inicio presenta un asistente para PC independiente, servidor POS con caja local o terminal cliente. El perfil se protege con DPAPI antes de iniciar operaciones; el cliente no almacena credenciales PostgreSQL. En servidor, el POS genera un certificado HTTPS para su IP y solicita permiso para abrir el puerto solo en redes privadas.
- **Completado en código:** la terminal cliente ingresa la IP, consulta el estado del servidor sin enviar credenciales, confirma su certificado una vez y almacena su huella SHA-256 protegida por Windows. El inicio de sesión normal del operador viaja por HTTPS; el servidor exige HTTPS desde la red y conserva la política de permisos por usuario. La migración anterior de credenciales de emparejamiento se conserva por compatibilidad, pero este flujo ya no la requiere.
- **Completado en código:** el actualizador distingue `Standalone`, `Server` y `ClientTerminal`. Solo respalda y migra la base en las dos primeras modalidades; conserva HTTPS y certificado en el servidor y no intenta conectarse a PostgreSQL desde una terminal cliente.
- **Pendiente de validación operativa antes de producción:** no se verificó con dos computadoras reales, una base PostgreSQL real ni reglas reales del Firewall de Windows. La confianza inicial fija el certificado que presenta la IP en el primer contacto; el primer contacto debe hacerse en la LAN privada correcta.
- **Alcance presente:** el modo servidor incluye interfaz POS local para que esa computadora también facture. Un perfil de servidor sin caja local queda fuera de esta entrega.

## Configuración de las computadoras

1. En la computadora principal, instala e inicia PostgreSQL. Ejecuta el POS y selecciona **Servidor POS + caja local**. El asistente detecta la IP local, genera el certificado y solicita permiso de Windows para permitir TCP 5155 solo en red privada.
2. Si no existe una conexión PostgreSQL protegida, el POS pide los datos y los valida antes de guardarlos. La ruta y clave del certificado se conservan cifradas en el perfil de Windows.
3. Reserva la IP del servidor en el router. Anota la dirección mostrada por el asistente.
4. En cada computadora caja, instala el paquete y selecciona **Terminal cliente**. Escribe la IP del servidor, confirma la identidad HTTPS una vez e inicia sesión con el usuario/PIN normal del operador. La terminal no necesita PostgreSQL.
5. Si cambia la IP o el certificado, al iniciar el cliente selecciona **No** en el aviso de conexión para reconfigurar la IP y revisar de nuevo la identidad del servidor.
6. Para actualizar, usa **Actualizar POS.cmd** desde el paquete nuevo y bajo la misma cuenta de Windows. En servidor, el actualizador respalda, detiene, migra y vuelve a iniciar el servicio con el mismo certificado; en terminal cliente solo reemplaza los archivos locales.

Cada terminal usa un identificador persistente. El cambio del certificado del servidor se detecta por la huella guardada localmente y requiere confirmación del usuario antes de continuar.

Si ya existía una instalación sin perfil de despliegue, el primer inicio actualizado pide elegir el modo una sola vez. Si eliges PC independiente, se reutiliza la conexión PostgreSQL protegida o heredada que ya existía.
