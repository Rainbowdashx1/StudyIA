<div align="center">

# StudyIA

**Convierte tus PDFs en evaluaciones personalizadas con inteligencia artificial**

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/WPF-Windows-0078D4?style=flat-square&logo=windows)](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)
[![C#](https://img.shields.io/badge/C%23-14.0-239120?style=flat-square&logo=csharp)](https://learn.microsoft.com/en-us/dotnet/csharp/)
[![GitHub Models](https://img.shields.io/badge/GitHub_Models-gpt--4o--mini-181717?style=flat-square&logo=github)](https://github.com/marketplace/models)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square)](LICENSE)

[Caracteristicas](#-caracteristicas) - [Requisitos](#-requisitos) - [Instalacion](#-instalacion) - [Uso](#-uso) - [Configuracion](#-configuracion-del-token) - [Arquitectura](#-arquitectura)

</div>

---

## Que es StudyIA?

StudyIA es una aplicacion de escritorio para Windows que analiza tus documentos PDF con IA y genera automaticamente preguntas de evaluacion. Puedes responderlas en un modo de quiz interactivo donde la propia IA califica cada respuesta de 0 a 100 y te da retroalimentacion inmediata.

Todo funciona de forma **local**: los PDFs nunca salen de tu equipo. Solo el texto extraido se envia a la API de GitHub Models para generar y evaluar preguntas.

Nota: Gran parte de este proyecto fue desarrollada con vibe coding apoyado por IA. En general, el resultado ha sido bueno, aunque no todo el código ha sido revisado completamente. Los cambios pequeños y más delicados los haré manualmente.

---

## Caracteristicas

### Gestion de PDFs
- Selecciona una carpeta y StudyIA la recuerda entre sesiones
- Calcula el **hash SHA-256** de cada archivo para detectar cambios
- Registra cada PDF en una base de datos SQLite local (un unico registro por archivo)
- Muestra el estado de cada PDF: **Nuevo**, **Sin cambios** o **Modificado**

### Generacion de preguntas con IA
- Extrae el texto de cada pagina usando **iText7**
- Envia el contenido a **GitHub Models** (`gpt-4o-mini`) para generar preguntas de evaluacion
- Cada pregunta incluye: texto, respuesta optima, numero de pagina y contexto
- Controla exactamente cuantas preguntas quieres y el sistema solo genera las que faltan

### Quiz de evaluacion
- Responde una pregunta a la vez en una interfaz limpia y enfocada
- La IA evalua tu respuesta de **0 a 100** con retroalimentacion en espanol
- Indicador visual por colores: verde >= 80, naranja >= 60, rojo < 60
- Atajo de teclado **Ctrl+Enter** para enviar respuestas
- Al terminar: panel de resultados con cada pregunta, tu respuesta, la puntuacion y la **media final**

### Persistencia total
- Base de datos SQLite local (`studyia.db`)
- Historial completo de respuestas con puntuacion y retroalimentacion
- Token de GitHub guardado de forma local

---

## Requisitos

| Requisito | Version minima |
|---|---|
| Windows | 10 / 11 |
| .NET Runtime | 10.0 |
| Cuenta de GitHub | Con acceso a [GitHub Models](https://github.com/marketplace/models) |

> **GitHub Models** esta disponible de forma gratuita para todos los usuarios de GitHub (con limites de uso en el plan Free).

---

## Instalacion

### Opcion A - Clonar y compilar

```bash
git clone https://github.com/Rainbowdashx1/StudyIA.git
cd StudyIA
dotnet build StudyIA/StudyIA.csproj -c Release
dotnet run --project StudyIA/StudyIA.csproj
```

### Opcion B - Visual Studio

1. Clona el repositorio o descarga el ZIP
2. Abre `StudyIA.sln` con **Visual Studio 2022 / 2026**
3. Restaura los paquetes NuGet (`dotnet restore`)
4. Pulsa **F5** para compilar y ejecutar

### Dependencias NuGet

| Paquete | Version | Uso |
|---|---|---|
| `itext7` | 9.5.0 | Extraccion de texto e imagenes de PDF |
| `Microsoft.Data.Sqlite` | 10.0.4 | Base de datos local |
| `OpenAI` | 2.2.0 | Comunicacion con GitHub Models API |

---

## Uso

### 1 - Seleccionar carpeta

Pulsa **Seleccionar carpeta** y elige la carpeta que contiene tus PDFs. StudyIA la recordara la proxima vez que abras la aplicacion.

Haz clic en **Escanear** para calcular el hash de cada archivo y registrarlo. La tabla mostrara el estado de cada PDF.

### 2 - Generar preguntas

Pulsa **Preguntas** para abrir la ventana de generacion.

1. Introduce tu **GitHub Token** (solo la primera vez) y pulsa **Guardar**
2. Indica cuantas preguntas quieres en total
3. El sistema muestra cuantas ya existen y cuantas nuevas se generaran
4. Pulsa **Generar preguntas** - la IA analiza cada PDF y guarda las preguntas en la BD

### 3 - Practicar

Pulsa **Practicar** para iniciar el quiz.

- Las preguntas se presentan en orden aleatorio
- Escribe tu respuesta y pulsa **Responder** (o **Ctrl+Enter**)
- La IA evalua tu respuesta y muestra la puntuacion con retroalimentacion
- Pulsa **Siguiente** para continuar
- Al terminar, veras el panel de resultados con tu puntuacion media

---

## Configuracion del token

StudyIA utiliza la **API de GitHub Models**, que requiere un Personal Access Token (PAT) de GitHub.

### Como obtenerlo

1. Ve a [github.com/settings/tokens](https://github.com/settings/tokens)
2. Haz clic en **Generate new token (classic)**
3. Dale un nombre descriptivo (ej. `StudyIA`)
4. **No necesita permisos especiales** - deja todos los scopes desmarcados
5. Genera el token y copialo

### Donde introducirlo

Abre la ventana **Preguntas** - campo superior - pega el token - **Guardar**.

El token se almacena localmente en la base de datos SQLite (`studyia.db`).

---

## Arquitectura

```
StudyIA/
|-- AppDatabase.cs                    # Capa de datos SQLite (Settings, PdfFiles, Questions, UserAnswers)
|-- CopilotService.cs                 # Integracion con GitHub Models API (generacion + evaluacion)
|-- PdfTextService.cs                 # Extraccion de texto por pagina con iText7
|-- PdfImageExtractor.cs              # Extraccion de imagenes de PDF
|
|-- MainWindow.xaml/.cs               # Ventana principal - escaneo y registro de PDFs
|-- GenerateQuestionsWindow.xaml/.cs  # Ventana de generacion de preguntas con IA
|-- QuizWindow.xaml/.cs               # Ventana de quiz y resultados finales
|
|-- PdfFileRecord.cs                  # Modelo: registro de archivo PDF
|-- QuestionRecord.cs                 # Modelo: pregunta generada
`-- UserAnswerRecord.cs               # Modelo: respuesta del usuario
```

### Base de datos (studyia.db)

```sql
Settings     -- clave/valor (carpeta guardada, token de GitHub)
PdfFiles     -- un registro por PDF (ruta, hash SHA-256, tamano, fecha)
Questions    -- preguntas vinculadas a un PdfFile (pagina, contexto, respuesta esperada)
UserAnswers  -- respuestas del usuario (puntuacion 0-1, retroalimentacion, fecha)
```

### Flujo de datos

```
PDF en disco
    |-> PdfTextService     extrae texto por pagina
    |-> CopilotService     genera preguntas via GitHub Models
    |-> AppDatabase        persiste Questions vinculadas al PdfFileId
    |-> QuizWindow         presenta preguntas al usuario
    |-> CopilotService     evalua respuesta 0-100
    `-> AppDatabase        persiste UserAnswer con score y feedback
```

---

## Contribuir

Las contribuciones son bienvenidas. Por favor:

1. Haz un **fork** del repositorio
2. Crea una rama: `git checkout -b feature/mi-mejora`
3. Haz commit de tus cambios: `git commit -m "feat: descripcion"`
4. Abre un **Pull Request**

---

## Licencia

Distribuido bajo la licencia MIT. Consulta el archivo [LICENSE](LICENSE) para mas detalles.

---

<div align="center">

Hecho con amor - [Rainbowdashx1](https://github.com/Rainbowdashx1)

</div>