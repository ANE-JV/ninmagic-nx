# Validación de ninmagic-nx 1.0.0

Fecha de preparación: 28 de octubre de 2026.

## Comprobaciones realizadas

- Compilación Release de los tres proyectos con .NET SDK 8.0.427: cero avisos y cero errores.
- Publicación portable Windows x64 con runtime .NET incluido.
- 66 comprobaciones de planificación, formato FAT32, MBR, CRC de la GPT interna, emuMMC estándar/antigua/múltiple, rutas, atributos, dispositivos excluidos, corrupción, cancelación y errores de vaciado final.
- Migración completa simulada desde una tarjeta de 4 GiB a una de 8 GiB, con archivos temporales y una emuMMC RAW sintética de 64 MiB. Verificación de datos RAW, GPT, marcadores, INI, archivos y atributos tras la restauración.
- Planificación con capacidades comerciales de 128 GB, 512 GB, 1 TB y 2 TB; rechazo de diseños incompatibles, solapamientos, capacidad insuficiente y sectores de 4 KiB.
- Comprobación independiente con pyfatfs 1.1.0: imágenes dispersas de 4 GiB y 128 GB generadas por el formateador, creación de carpeta con nombre largo y archivo de 1,28 MB, cierre, reapertura y SHA-256 correcto. pyfatfs es una herramienta de prueba y no se distribuye en la aplicación.
- Inventario Windows real e inspección posterior de una microSD FAT32/MBR con emuMMC RAW, en modo de solo lectura: geometría, CRC de las dos GPT internas, igualdad de sus tablas y referencias INI/raw_based correctos. La MBR y las referencias examinadas permanecieron iguales. La comprobación de archivos de Windows sin reparación terminó sin problemas.
- Inicio del ejecutable y comprobación del árbol accesible de la interfaz Windows. Renderizado e inspección visual de las dos pantallas WPF, también al tamaño mínimo.

## Lo que estas pruebas no demuestran

No se ha escrito una microSD física. La prueba real posterior se limitó a leer la tarjeta de origen; no verificó todos los sectores NAND ni el contenido de cada juego o partida. No se han ejercitado con elevación el bloqueo/desmontaje, reconocimiento de la tabla nueva, formateo exFAT y montaje final de un dispositivo real. Tampoco se ha arrancado una Nintendo Switch desde una tarjeta migrada.

Por ello, la versión se entrega como candidata para validación física. No se afirma que funcione con cualquier lector, tarjeta o tamaño ni que esté libre de errores. Se debe conservar la tarjeta original y probar la nueva en la misma consola antes de reutilizarla o borrar la copia.

Las pruebas no contienen juegos, datos personales, claves ni imágenes NAND de una consola real. Todos los destinos de escritura de las pruebas son memoria o archivos temporales del PC.

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

La descarga candidata conserva el informe original del momento de empaquetado. Este documento recoge también la inspección física de lectura realizada posteriormente; el ejecutable de la aplicación no cambió.

`tools/BackupRunner` permite ejecutar el mismo motor de copias en segundo plano con una huella MBR, número de disco y capacidad exactos. Su adaptador rechaza obtener sesiones de escritura. Publica progreso JSON de forma atómica y solo marca `success` después de que `CreateAsync` devuelva una copia completa y verificada. El archivo `cancel.flag` junto al JSON solicita cancelación ordenada. Los informes, las copias y sus inventarios son privados y quedan fuera del repositorio.
