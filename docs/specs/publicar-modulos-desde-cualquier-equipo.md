# Publicación firmada de módulos desde cualquier equipo

## Criterios de aceptación

1. El publicador conserva el flujo de firma por certificado de Windows que ya usan los equipos de desarrollo.
2. Un runner Windows prepara un ZIP sin firma con inventario SHA-256 y un `signerKeyId`; el proceso de firma rechaza cualquier diferencia entre el inventario y los archivos del ZIP.
3. La CLI firma el índice exacto con RSA-PSS y SHA-256 desde una clave privada PEM, y el paquete final pasa `ModulePackageVerifier` con la clave pública correspondiente.
4. El firmador rechaza paquetes ya firmados, paquetes alterados y claves RSA menores a 2048 bits. Una clave pública distinta no verifica la firma.
5. La clave pública nueva se incorpora de forma aditiva al anillo de confianza; la clave anterior permanece intacta durante la transición.
6. La clave privada permanece en el VPS, fuera del repositorio, del workspace del runner Windows y de los artefactos de GitHub Actions.
7. La publicación se inicia manualmente desde `main`. El runner privado solo atiende el job de firma; las compilaciones WPF suceden en un runner Windows hospedado por GitHub.
8. La opción `all` prepara y firma los tres módulos distribuibles del catálogo (`warehouses`, `thermal-receipts` y `advanced-reports`) en una sola ejecución, y falla si falta o sobra algún paquete.

## Historia

Como responsable del POS, quiero preparar módulos en cualquier PC o VPS y publicarlos con una firma que los servidores ya confían, sin depender del certificado privado instalado en una PC concreta.

## Flujo

`Windows GitHub-hosted (compila y prepara ZIP sin firma) -> artefacto temporal -> runner privado Ubuntu (valida y firma) -> artefactos públicos: ZIP, PEM público y SHA-256`.

El registro del runner, el endurecimiento del VPS y la prueba del flujo real de GitHub son pasos operativos separados de la prueba local del publicador. Nunca se debe afirmar que están terminados sin evidencia de cada paso.
