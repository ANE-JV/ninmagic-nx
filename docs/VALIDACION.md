# Validación de ninmagic-nx 1.5.1

Fecha de preparación: 29 de octubre de 2026.

## Actualización 1.5.1

Se añaden letras de unidad al selector, detalles y confirmación, conservando las rutas GUID y la identidad física. Cinco pruebas nuevas cubren letras múltiples, volúmenes sin letra, manifiestos anteriores y conservación de identidad. La suite contiene 156 comprobaciones. Otras 16 comprobaciones WPF verifican la visibilidad de las letras, el acceso al botón en tamaño normal/mínimo, los avisos, las guardas de inicio y el contador durante la preparación. El empaquetado exige que ambas suites terminen sin fallos.

Los avisos de permisos o tarjeta sin seleccionar quedan visibles junto al botón. El tiempo transcurrido se actualiza durante la preparación inicial y los registros de sesión incluyen la recepción del clic. El usuario informó de un botón que no responde en otro PC; esa incidencia no se ha reproducido y no se considera resuelta sin comprobarlo allí. Las pruebas de interacción de WPF se ejecutan sin mostrar ventanas ni enumerar tarjetas.

## Estado de la versión 1.5

Esta actualización incorpora el logo original JV en una esquina y un fondo de circuitos cian y rojos inspirado en la referencia visual del usuario. Conserva los pasos, los tiempos estimados y la elección de verificar los archivos al terminar. No añade compatibilidad Android/Linux ni reanudación de operaciones.

La versión 1.5 ha superado nuevamente las 151 comprobaciones automáticas, sin fallos. Se ha compilado la interfaz y se han renderizado e inspeccionado sus pantallas a tamaño normal y mínimo, incluyendo el logo, fondo, contador y opción de verificación desactivada. El logo se incorpora como recurso de la aplicación y no depende de archivos externos. El fondo es vectorial y estático.

## Comprobaciones automáticas y validaciones acumuladas

- 151 comprobaciones automáticas superadas, con destinos simulados y archivos temporales del PC.
- Verificación de archivos activada por defecto; omisión explícita con 5 pasos sin RAW y 7 con una RAW. El resultado y el registro distinguen los archivos no verificados; la prueba de corrupción del destino demuestra que este modo no puede certificar su contenido.
- En el modo sin lectura final se conservan fechas, atributos, referencias emuMMC, validación de la copia del PC, hashes durante copia y verificación RAW. Corrupción de la copia o RAW, cancelación durante el vaciado y error de vaciado final impiden un resultado completado.
- Estimación de tiempo con reloj simulado: espera de muestras suficientes, velocidades separadas por dispositivo y sentido, cambio de fase, ralentización, ausencia de avance, recuperación, reinicio y finalización explícita.
- Progreso de restauración con 0, 1 y 2 particiones RAW: 6, 8 y 10 pasos respectivamente. Copia con 1 y 2 RAW: 6 y 7 pasos. Comprobaciones independientes y previas de un paso.
- Ninguna finalización anticipada ante corrupción, cancelación, error de escritura o fallo del vaciado final. La verificación interna de la copia no marca el traslado como terminado.
- Compilación Release de los tres proyectos con .NET SDK 8.0.427: cero avisos y cero errores.
- Publicación portable Windows x64 con runtime .NET incluido.
- Comprobaciones de planificación, formato FAT32, MBR, CRC de la GPT interna, emuMMC estándar/antigua/múltiple, rutas, atributos, dispositivos excluidos, corrupción, cancelación y errores de vaciado final.
- Integración NTFS→FAT32 probada con manifiestos exFAT: rechazo de archivos individuales demasiado grandes antes de escribir, restauración de archivos y comprobación de que no se recorre toda la capacidad del destino. Las pruebas utilizan el constructor público del motor, sin sustitutos del comprobador de capacidad.
- Adaptador Windows comprobado con archivos temporales: acceso al último sector sin lectura adelantada y liberación de todos los bloqueos aunque falle el cierre.
- Migración completa simulada desde una tarjeta de 4 GiB a una de 8 GiB, con archivos temporales y una emuMMC RAW sintética de 64 MiB. Verificación de datos RAW, GPT, marcadores, INI, archivos y atributos tras la restauración.
- Planificación con capacidades comerciales de 128 GB, 512 GB, 1 TB y 2 TB; rechazo de diseños incompatibles, solapamientos, capacidad insuficiente y sectores de 4 KiB.
- Comprobación independiente con pyfatfs 1.1.0: imágenes dispersas de 4 GiB y 128 GB generadas por el formateador, creación de carpeta con nombre largo y archivo de 1,28 MB, cierre, reapertura y SHA-256 correcto. pyfatfs es una herramienta de prueba y no se distribuye en la aplicación.
- Inventario Windows real e inspección posterior de una microSD FAT32/MBR con emuMMC RAW, en modo de solo lectura: geometría, CRC de las dos GPT internas, igualdad de sus tablas y referencias INI/raw_based correctos. La MBR y las referencias examinadas permanecieron iguales. La comprobación de archivos de Windows sin reparación terminó sin problemas.
- Renderizado e inspección visual de las pantallas WPF, el panel de pasos/tiempos y la finalización al tamaño mínimo. El renderizador no enumera ni abre tarjetas.

## Lo que estas pruebas no demuestran

El usuario ha confirmado el funcionamiento de la migración realizada con la versión 1.2 y de la tarjeta nueva en su consola. Esta confirmación corresponde a un caso de uso real de esa versión; no es una certificación universal ni una prueba física de la versión 1.5.

Las pruebas automáticas de esta actualización no escriben tarjetas físicas. La versión 1.5 no se ha validado todavía mediante otro traslado real seguido de arranque en Nintendo Switch. Las comprobaciones simuladas y la confirmación del usuario no demuestran que funcionen todos los lectores, el montaje o el formateo exFAT de cualquier dispositivo real.

Por ello, la versión se entrega como candidata para validación física. No se afirma que funcione con cualquier lector, tarjeta o tamaño ni que esté libre de errores. Se debe conservar la tarjeta original y probar la nueva en la misma consola antes de reutilizarla o borrar la copia.

Las pruebas automáticas no contienen juegos, datos personales, claves ni imágenes NAND de una consola real. Todos sus destinos de escritura son memoria o archivos temporales del PC. No se incluyen datos privados de la migración confirmada por el usuario.

## Reproducir

```powershell
dotnet run --project tests/Ninmagic.Nx.Tests -c Release
dotnet run --project tests/Ninmagic.Nx.Tests -c Release -- --inventory
```

El segundo comando añade únicamente inventario de lectura. El script `tools/build.ps1` ejecuta primero las pruebas y solo después empaqueta el ejecutable.

Prueba independiente FAT32 (opcional, archivos dispersos en un volumen NTFS; no monta discos):

```powershell
python -m pip install -r tools/Fat32Qa/requirements.txt
dotnet run --project tools/Fat32Qa -c Release
python tools/Fat32Qa/verify_pyfatfs.py
```

El generador usa creación exclusiva: si los archivos de prueba ya existen, se detiene sin sobrescribirlos. Su tamaño lógico es de 4 GiB y 128 GB; son imágenes dispersas, no copias de dispositivos.

Para renderizar la interfaz sin enumerar tarjetas:

```powershell
dotnet run --project tools/RenderUi -c Release -- artifacts/ui
```

El proyecto `tools/InspectCard` es un diagnóstico independiente y opcional. Recibe número de disco, tamaño exacto en bytes y ruta absoluta de un informe JSON nuevo en otro disco. Requiere administrador y solo abre el origen en lectura. No llama a formateo, restauración ni sesiones de escritura. Guarda una copia mínima de las referencias y el informe en el PC; estos resultados son privados y no deben añadirse al repositorio.

La versión 1.3 añade pasos y estimaciones sin nuevas lecturas para calcular el tiempo. La 1.4 permite omitir la lectura final de archivos del destino, manteniendo validación de la copia, verificación RAW y conversión NTFS a FAT32. La 1.5 incorpora el logo original y el fondo de circuitos; conserva esas funciones y sus límites. Con la opción recomendada activada mantiene todas las comprobaciones de 1.2. Ningún modo ejecuta una prueba destructiva de toda la capacidad. Las copias de formato 1, incluidas las creadas por 1.0.0, siguen siendo compatibles.

`tools/BackupRunner` permite ejecutar el mismo motor de copias en segundo plano con una huella MBR, número de disco y capacidad exactos. Su adaptador rechaza obtener sesiones de escritura. Publica progreso JSON de forma atómica y solo marca `success` después de que `CreateAsync` devuelva una copia completa y verificada. El archivo `cancel.flag` junto al JSON solicita cancelación ordenada. Los informes, las copias y sus inventarios son privados y quedan fuera del repositorio.
