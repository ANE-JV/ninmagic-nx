# ninmagic-nx

Aplicación portable en español para Windows 10/11 de 64 bits. Permite guardar una copia verificada de una microSD con Atmosphère y emuMMC en el PC y restaurarla en una tarjeta de mayor capacidad, ampliando la partición de archivos.

**Estado: versión 1.1 candidata para validación física.** La compilación, las pruebas automáticas y las pruebas sobre imágenes de disco han pasado. Se ha inspeccionado una tarjeta original real en modo de solo lectura; todavía no se ha restaurado una tarjeta física ni arrancado una Nintendo Switch con ella. Esto no es una garantía de ausencia de errores ni de compatibilidad con cualquier tarjeta. Conserva la original hasta verificar la nueva en tu consola.

## Descargar y usar

El ejecutable portable `ninmagic-nx.exe` se encuentra en [Releases](https://github.com/ANE-JV/ninmagic-nx/releases). El repositorio es privado: debes iniciar sesión con una cuenta autorizada para descargarlo. No necesitas instalar .NET. La aplicación no requiere Internet ni envía datos.

1. Apaga completamente la consola. Conecta la microSD original al PC mediante un lector USB/SD/MMC. No abras ni modifiques sus archivos durante la copia.
2. Abre `ninmagic-nx.exe` y pulsa **Abrir como administrador**. Windows solicitará permiso para acceder a las tarjetas. El ejecutable no está firmado con un certificado comercial.
3. En **Crear copia**, selecciona la tarjeta original y una carpeta del PC. Necesitas espacio para todos los archivos utilizados **más el tamaño completo de las particiones emuMMC**. La copia se guarda en una carpeta nueva de una unidad NTFS o exFAT.
4. Espera a **Copia terminada y verificada**. Retira la tarjeta original, conecta la nueva y pulsa **Actualizar**. Solo necesitas un lector.
5. En **Restaurar en otra SD**, selecciona la carpeta de copia que contiene `manifest.json` y la tarjeta nueva. La aplicación comprobará la copia y mostrará el disco exacto que va a borrar. Debes escribir `BORRAR DISCO N` para confirmar.
6. La aplicación escribe patrones distintos en toda la capacidad de la tarjeta nueva y después la relee completa. Puede tardar varias horas. Si falla, se detiene antes de crear las particiones y restaurar. Si el destino estaba en NTFS y supera la prueba, lo prepara en FAT32. Espera a **Traslado terminado y verificado**. Expulsa la tarjeta de forma segura, insértala en la **misma consola** y comprueba el arranque, los juegos y las partidas. Conserva la original y la copia del PC.

**Todo el contenido anterior de la tarjeta de destino se elimina.** La aplicación abre la tarjeta original solo para lectura y no la formatea. Una cancelación o un error deja el destino incompleto; vuelve a restaurarlo desde el principio. Las copias incompletas no se aceptan como origen.

## Qué se conserva

- Todos los archivos y carpetas accesibles de la partición FAT32/exFAT, incluidos los ocultos, aplicaciones, configuraciones, juegos instalados y copias de partidas.
- Cada partición emuMMC RAW reconocida, incluidos BOOT0, BOOT1 y la NAND emulada, copiada completa y comprobada con SHA-256. Las partidas guardadas dentro de esa emuMMC se conservan en la imagen.
- emuMMC por archivos: se copian sus archivos junto con el resto de la tarjeta.
- Nombres, fechas de modificación y atributos de archivos/directorios, incluido el atributo Archive necesario para carpetas de archivos divididos.
- Referencias `sector` en `emuMMC/emummc.ini` y marcadores `emuMMC/<nombre>/raw_based`, ajustados al trasladar particiones. Se preservan las demás opciones.

Las partidas de **sysMMC** están en la memoria interna de la consola, no en la microSD. Esta aplicación no copia esa memoria ni traslada juegos o partidas a otra consola. No incluye juegos, claves, firmware ni herramientas de descifrado.

Se excluyen únicamente las carpetas de mantenimiento de Windows `System Volume Information` y `$RECYCLE.BIN` en la raíz; las exclusiones se registran en el manifiesto. No es una imagen forense sector por sector de la tarjeta completa: no conserva espacio libre, archivos borrados ni código de arranque ajeno al diseño admitido.

## Compatibilidad comprobada por el programa

| Elemento | Admitido |
|---|---|
| PC | Windows 10/11 x64, administrador para trabajar con tarjetas |
| Lectores | USB, SD o MMC con identidad verificable y sectores lógicos de 512 bytes |
| Tabla de particiones | MBR, primera partición FAT32 o exFAT |
| emuMMC RAW | Hasta tres particiones reconocibles, estándar Hekate y diseño antiguo sin separación de 16 MiB |
| emuMMC por archivos | Configuración Hekate estándar; archivos conservados |
| Destino | Capacidad real igual o mayor que el origen, dentro del límite MBR de 2 TiB; una tarjeta comercial de 2 TB cabe dentro de ese límite |
| Espacio adicional | Se asigna a FAT32/exFAT; el tamaño interno de emuMMC no cambia |
| Formato de archivos | Si el destino estaba en NTFS, se crea FAT32; en los demás casos se conserva FAT32 o exFAT del origen |

Como origen se rechazan GPT/híbrido, Linux/Android, particiones extendidas o desconocidas, sectores de 4 KiB, rutas emuMMC RAW personalizadas fuera de `emuMMC/<nombre>`, estructuras dañadas, falta de capacidad y dispositivos sin identidad suficiente. FAT32 de destino necesita al menos unos 2,1 GiB para usar clústeres de 32 KiB. Una tarjeta nominalmente igual puede tener menos sectores reales y ser rechazada.

## Integridad y límites

La aplicación compara SHA-256 de las copias y de los datos restaurados. Comprueba referencias emuMMC, geometría de particiones y CRC de la GPT interna de emuMMC RAW. Bloquea discos de Windows y buses internos, revalida la identidad del destino y bloquea/desmonta sus volúmenes antes de escribir. Solo informa de éxito tras completar copia, comprobación y vaciado de escritura.

La prueba completa de capacidad usa patrones aleatorios dependientes de su posición: primero escribe todo el dispositivo, vacía la escritura y después relee todo. Así detecta errores y capacidades falsas que solapan direcciones. Un fallo detiene la restauración y avisa de posibles problemas de tarjeta, lector o conexión. Un resultado correcto solo acredita los bytes comprobados en esa ejecución: no certifica marca, vida útil ni funcionamiento o rendimiento de emuMMC en la consola. Las velocidades secuenciales medidas incluyen el lector, USB, PC y la comprobación; una lectura inferior a 60 MB/s se informa sin declarar la tarjeta defectuosa por ello. El montaje, bloqueo y formateo de dispositivos físicos de Windows todavía requieren validación práctica. No desconectes el lector durante una operación. Nunca aceptes formatear la partición oculta si Windows lo propone.

La conversión de un destino NTFS a FAT32 se bloquea antes de escribir si la copia contiene archivos mayores de 4 GiB menos 1 byte; no se recortan ni dividen automáticamente. La prueba de capacidad borra el contenido anterior del destino incluso si luego falla o se cancela. No se ejecuta simplemente al insertar una tarjeta: requiere seleccionar el destino y confirmar su borrado.

Los registros se guardan en `%LOCALAPPDATA%\NinmagicNX\Logs`; la última restauración y la prueba de tarjeta (`ultima-prueba-tarjeta.json`) se registran en la carpeta de copia. El informe conserva capacidad comprobada, velocidades y, si falla, fase y posición del error cuando están disponibles. Una copia contiene datos privados de tu consola: guárdala como información personal, no la subas a este repositorio.

## Compilar y verificar

Requiere .NET SDK 8 para compilar, Windows y PowerShell para la aplicación. No hay dependencias NuGet externas en el programa.

```powershell
.\tools\build.ps1
```

El script ejecuta las pruebas sobre dispositivos simulados y genera el ejecutable portable y el ZIP en `dist`. **No escribe en discos físicos.** La prueba opcional `--inventory` consulta dispositivos sin modificarlos. El detalle y las limitaciones están en [docs/VALIDACION.md](docs/VALIDACION.md).

La implementación se divide en `Ninmagic.Nx.Core` (planificación, formatos y copias), `Ninmagic.Nx.Windows` (dispositivos y montaje) y `Ninmagic.Nx.App` (asistente WPF). Las pruebas usan discos dispersos simulados y carpetas temporales, con comprobaciones de corrupción, cancelación y preservación de metadatos.

## Referencias técnicas

El formato emuMMC se contrastó con las implementaciones originales de [Hekate](https://github.com/CTCaer/hekate/blob/master/bootloader/storage/emummc.c), su [gestor emuMMC](https://github.com/CTCaer/hekate/blob/master/nyx/nyx_gui/frontend/gui_emummc_tools.c) y su [gestor de particiones](https://github.com/CTCaer/hekate/blob/master/nyx/nyx_gui/frontend/gui_tools_partition_manager.c). La integración Windows sigue la documentación de [bloqueo de volúmenes](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_lock_volume) e [identidad MSFT_Disk](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disk). No se ha incorporado código fuente de Hekate ni Atmosphère al ejecutable.

El procedimiento de prueba completa se contrastó con el enfoque documentado por [F3](https://fight-flash-fraud.readthedocs.io/en/stable/introduction.html). La referencia de velocidad de [Nintendo](https://www.nintendo.com/au/support/articles/which-microsd-cards-are-supported/) es una recomendación de lectura secuencial para Switch, no una certificación de emuMMC.

Proyecto independiente de las otras aplicaciones NinMagic. Sin afiliación con Nintendo, Hekate o Atmosphère.
