# SPEC: agente de IA estilo WhatsApp para pequeños negocios (.NET + React)

Documento para el agente de código que va a construir este proyecto. Leelo entero antes de escribir código.

## 0. Cómo trabajar con este documento

- **Construí por milestones (sección 15)** y en orden. No avances al siguiente hasta que el actual cumpla sus criterios de aceptación, con los tests en verde y la app funcionando.
- **Al cerrar cada milestone:** corré la app y probá el flujo cambiado de punta a punta (no alcanza con que pasen los tests), hacé commit y dejá una nota corta en `docs/progress.md` con lo hecho y lo pendiente.
- **Las APIs de librerías cambian.** Antes de usar Microsoft.Extensions.AI, EF Core o el SDK de OpenAI, verificá las firmas contra la documentación vigente (sección 18). Los snippets de este documento son orientativos, no código copiable.
- **El dueño del repo (Agustín) tiene que poder explicar cada parte en una entrevista técnica.** Código idiomático y directo, sin abstracciones de más. Cada decisión no obvia va en `docs/decisions.md` (formato ADR corto: contexto, decisión, alternativas descartadas).
- **Si algo de esta spec es ambiguo o choca con la realidad** (una API no existe, un servicio gratis dejó de serlo), no lo inventes: frená y preguntá. Las decisiones que ya sabemos abiertas están en la sección 17.
- **Idioma:** código, comentarios, README, commits y UI en inglés. El bot responde en el idioma del usuario (inglés o español).

## 1. Objetivo

Proyecto de portfolio público para conseguir trabajo remoto como **backend .NET developer con experiencia en agentes de IA**. Tiene que demostrar, en algo que un recruiter o entrevistador pueda abrir y probar en 2 minutos:

1. **Backend .NET moderno:** ASP.NET Core, EF Core, PostgreSQL, tests.
2. **Agente de IA real:** tool calling con herramientas que leen y escriben datos de verdad, no un chatbot que solo genera texto.
3. **IA en producción:** traza de cada paso (llamadas al modelo y a herramientas, tokens, latencia, costo), human-in-the-loop, guardrails, rate limiting y evals.
4. **Frontend en React** (el dueño viene de Angular; esto suma React al perfil).

Lo que diferencia al proyecto es el **panel "Show agent trace"**: cualquier visitante ve en vivo qué hizo el agente para responder.

## 2. Qué es el producto

Un asistente con estética de WhatsApp Web que atiende clientes de un pequeño negocio. El visitante elige uno de tres rubros, cada uno con su persona, catálogo y reglas:

| Rubro | Persona | Negocio | Qué resuelve |
|---|---|---|---|
| Real estate | Sofia | PrimeHaven Realty | Consultas de propiedades, filtros por precio y tipo, agenda de visitas |
| Dental clinic | Alex | Smile Dental Clinic | Tratamientos y precios, disponibilidad, turnos |
| Restaurant | Nico | Luigi's Pizzeria | Menú, pedido con total calculado, delivery |

El agente puede:
- buscar en el catálogo;
- consultar disponibilidad real;
- registrar reservas o pedidos (quedan pendientes de confirmación del negocio);
- derivar a una persona cuando corresponde.

Todo queda persistido y trazado.

## 3. Antecedente: la v1 y qué hay que corregir

La v1 es [agustinrojas1/whatsapp-ai-chatbot-demo](https://github.com/agustinrojas1/whatsapp-ai-chatbot-demo) (Next.js 15 + Vercel AI SDK + Gemini/Groq), publicada en `whatsapp-ai-chatbot-demo.vercel.app`. **Este es un repo nuevo:** la v1 queda como está. Reutilizá de la v1:
- la **UI estilo WhatsApp** (`app/page.tsx`, `app/globals.css`, tokens de `tailwind.config.ts`), portada a React + Vite;
- el **contenido de las personas** (`PERSONAS` y `buildPrompt` en `app/api/chat/route.ts`): catálogos, reglas de formato WhatsApp y few-shots. El catálogo pasa a la base y las reglas de honestidad se reescriben, porque ahora el agente sí registra datos.

Problemas de la v1 que esta versión tiene que resolver (verificados probando la demo el 2026-10-09):

| Problema en v1 | Cómo se resuelve acá |
|---|---|
| Sin herramientas: "registré tu visita" es solo texto, no se guarda nada | Tools reales con persistencia (sección 7) |
| Idioma inconsistente: a mensajes en inglés responde en español aunque el prompt lo prohíbe | Detección de idioma determinística por turno, inyectada al modelo, más evals (secciones 7.3 y 12) |
| Rate limit solo del lado del cliente (se saltea con un `curl`) | Rate limiting en el servidor (sección 9) |
| Handoff con un tag de texto (`[HANDOFF_TRIGGERED]`) parseado del output | Tool `handoff_to_human` que crea un registro (sección 7) |
| Botón "Hire me on Fiverr" y sin autoría | Sin CTA comercial; footer "Built by Agustín Rojas" con GitHub y LinkedIn |
| Guardrails solo en el prompt | Límites en código: herramientas acotadas al negocio de la sesión, máximo de iteraciones, validación de argumentos |

## 4. Alcance

**MVP (hay que hacerlo):**
- Los 3 rubros con datos seed realistas.
- Chat con streaming.
- 5 tools (sección 7.2).
- Traza persistida y panel en la UI.
- Vista `/admin` de solo lectura con reservas y derivaciones.
- Rate limiting, tests, evals, Docker Compose local, README con GIF y deploy.

**Fuera de alcance:**
- Integración real con WhatsApp (Cloud API).
- Login de usuarios finales, pagos, emails reales, multi-tenant real.
- RAG o base vectorial: el catálogo es chico y se busca por filtros. Justificalo en `docs/decisions.md`; es una buena respuesta de entrevista.
- Fine-tuning o modelos locales.

## 5. Arquitectura

```mermaid
flowchart LR
  U[Browser<br/>React + Vite] -- REST + SSE --> API[ASP.NET Core API]
  API --> AG[AgentService]
  AG --> CC[IChatClient pipeline<br/>Tracing → FunctionInvocation → OpenAI-compatible]
  CC --> LLM[(LLM provider<br/>Gemini / Groq)]
  AG --> T[Tools]
  T --> DB[(PostgreSQL)]
  AG --> TR[TraceRecorder]
  TR --> DB
  ADM[/admin view/] -- REST --> API
```

**Layout del repo (monorepo):**

```
/api
  /src/Chatbot.Api          ASP.NET Core minimal API (endpoints, DI, config)
  /src/Chatbot.Core         dominio, AgentService, tools, tracing (sin dependencias web)
  /src/Chatbot.Infrastructure  EF Core DbContext, migraciones, seed, cliente LLM
  /tests/Chatbot.Tests      unit + integración
/evals
  /Chatbot.Evals            consola que corre los casos contra el modelo real
  /cases/*.yaml             casos de evaluación
/web                        React 19 + Vite + TypeScript + Tailwind
/docs                       decisions.md, progress.md, architecture.md
docker-compose.yml          postgres + api + web
.env.example
README.md
```

Si tres proyectos .NET resultan excesivos para el tamaño real del código, está bien unir Core e Infrastructure. Dejá la decisión en `docs/decisions.md`.

## 6. Stack

| Capa | Tecnología | Notas |
|---|---|---|
| Runtime | **.NET 10 (LTS)** | `Nullable` habilitado, `TreatWarningsAsErrors` en Core |
| API | ASP.NET Core minimal APIs | SSE para el streaming |
| IA | `Microsoft.Extensions.AI` + `Microsoft.Extensions.AI.OpenAI` | `IChatClient`, `ChatClientBuilder`, `UseFunctionInvocation()`, `AIFunctionFactory` |
| LLM | Cualquier endpoint compatible con OpenAI, configurable | Default **Gemini** (`https://generativelanguage.googleapis.com/v1beta/openai/`, free tier); fallback **Groq** (`https://api.groq.com/openai/v1`). Ver sección 8 |
| Datos | EF Core 10 + Npgsql, **PostgreSQL 17** | `jsonb` para atributos del catálogo y payloads de la traza |
| Tests | xUnit, `WebApplicationFactory`, Testcontainers (PostgreSQL) | Un `IChatClient` falso para tests deterministas |
| Front | React 19, Vite, TypeScript strict, Tailwind, Vitest | Sin framework de estado: hooks y context alcanzan |
| Infra | Docker Compose local; Dockerfile para la API | Deploy en la sección 13 |

## 7. Diseño del agente

### 7.1 Modelo de datos

Nombres orientativos; ajustalos a convenciones de EF Core.

| Entidad | Campos clave | Para qué |
|---|---|---|
| `Business` | `Id`, `Niche` (enum), `Name`, `PersonaName`, `PersonaPrompt`, `Greeting`, `QuickReplies` (jsonb), `Timezone`, `Currency` | Un registro por rubro; el prompt vive en la base, no hardcodeado |
| `Offering` | `Id`, `BusinessId`, `Kind` (Property / Treatment / MenuItem), `Name`, `Description`, `Price`, `PriceUnit` (`month`, `sale`, `item`), `Attributes` (jsonb: barrio, ambientes, apto gluten-free, etc.), `IsActive` | Catálogo |
| `AvailabilitySlot` | `Id`, `BusinessId`, `OfferingId?`, `StartsAt`, `EndsAt`, `Capacity`, `Booked` | Turnos y visitas. El seed genera slots de los próximos 14 días según el horario de cada negocio |
| `Booking` | `Id`, `BusinessId`, `SessionId`, `SlotId?`, `OfferingId?`, `CustomerName`, `CustomerPhone`, `Items` (jsonb, para pedidos), `Total?`, `Status` (`PendingConfirmation` / `Confirmed` / `Cancelled`), `IdempotencyKey` (único), `CreatedAt` | Reservas, turnos y pedidos |
| `HandoffRequest` | `Id`, `SessionId`, `Reason`, `Summary`, `Status` (`Open` / `Closed`), `CreatedAt` | Derivación a una persona |
| `ChatSession` | `Id`, `BusinessId`, `Status` (`Active` / `AwaitingHuman` / `Closed`), `Language?`, `CreatedAt`, `LastActivityAt`, `ClientIpHash` | Una conversación |
| `ChatMessage` | `Id`, `SessionId`, `Role`, `Content`, `CreatedAt` | Historial para reconstruir el contexto |
| `AgentRun` | `Id`, `SessionId`, `UserMessageId`, `Model`, `Status` (`Ok` / `Error`), `TokensIn`, `TokensOut`, `CostUsd`, `DurationMs`, `StartedAt` | Una ejecución del agente por mensaje del usuario |
| `TraceEvent` | `Id`, `RunId`, `Sequence`, `Type` (enum cerrado: `LlmCall`, `ToolCall`, `Guardrail`, `Handoff`, `Error`), `Name`, `Input` (jsonb), `Output` (jsonb), `TokensIn?`, `TokensOut?`, `DurationMs`, `CreatedAt` | Cada paso de la ejecución |
| `ModelPricing` | `Model`, `InputPerMillionUsd`, `OutputPerMillionUsd` | Calcular el costo sin hardcodear tarifas. Los modelos gratis pueden tener precio 0, pero mostrá igual lo que costaría con tarifa paga, configurable |

**Invariantes que tienen que estar en la base, no solo en el código:**
- `Booking.IdempotencyKey` único: un reintento no duplica la reserva.
- `Slot.Booked <= Slot.Capacity`, controlado con concurrencia optimista o un `UPDATE ... WHERE booked < capacity`.
- `TraceEvent.Type` es un enum cerrado. Si cada tool inventa su tipo, el panel no puede agregar métricas.

### 7.2 Tools

Cada tool es un método C# con `[Description]` claros, registrado con `AIFunctionFactory`. **Todas reciben el `BusinessId` y el `SessionId` desde el contexto de la sesión en el servidor, nunca como argumento del modelo.** El modelo no puede leer ni escribir datos de otro negocio. Si los argumentos no son válidos, la tool devuelve un error legible para el modelo (no lanza una excepción): así el agente puede corregirse.

| Tool | Argumentos (del modelo) | Hace | Devuelve |
|---|---|---|---|
| `search_catalog` | `query?`, `maxPrice?`, `minPrice?`, `kind?`, `filters?` (por ejemplo `{ "neighborhood": "Palermo", "rooms": 2 }`) | Busca ofertas activas del negocio de la sesión | Lista corta (máx. 5) con id, nombre, precio y atributos clave |
| `get_offering_details` | `offeringId` | Detalle de una oferta | Objeto completo o "not found" |
| `check_availability` | `offeringId?`, `fromDate`, `toDate`, `partOfDay?` (`morning` / `afternoon` / `evening`) | Slots libres en el rango (máximo 14 días) | Hasta 6 slots con id y horario en la zona horaria del negocio |
| `create_booking` | `slotId?`, `offeringId?`, `items?` (pedidos: `[{offeringId, qty, options}]`), `customerName`, `customerPhone`, `notes?` | Valida y crea el `Booking` en `PendingConfirmation`, con idempotency key derivada de sesión + slot o ítems. En pedidos calcula el total en el servidor | Id, estado y resumen. **Nunca "confirmado"** |
| `handoff_to_human` | `reason`, `summary` | Crea el `HandoffRequest` y pasa la sesión a `AwaitingHuman` | Confirmación. La UI muestra un banner de sistema |

**Reglas de comportamiento** (van en el system prompt y se verifican con evals):
- Para afirmar precios o disponibilidad, el agente tiene que haber llamado a la tool correspondiente en esa ejecución. No responde de memoria.
- Antes de `create_booking`, el agente repite el resumen (qué, cuándo, nombre, teléfono) y pide confirmación explícita. Recién con un "sí" llama a la tool.
- Después de crear la reserva dice que quedó **registrada y pendiente de confirmación** del negocio. Nunca "confirmada".
- `handoff_to_human` se usa cuando el usuario pide una persona, o cuando pregunta algo fuera del catálogo y acepta la derivación. No se usa para small talk ni para mensajes incomprensibles.
- Con la sesión en `AwaitingHuman`, el agente avisa que una persona va a continuar y no inicia flujos nuevos.

### 7.3 Idioma (bug conocido de la v1)

No confíes solo en el prompt.
- **Antes de cada ejecución:** detectá el idioma del último mensaje del usuario de forma determinística (heurística de stopwords o una librería chica; solo hace falta `en` o `es`, con fallback al idioma anterior de la sesión si el mensaje es ambiguo, por ejemplo "ok" o "2").
- **Inyectalo como último mensaje de sistema:** `Reply ONLY in {Spanish|English}. Translate catalog details if needed.`
- **Guardalo** en `ChatSession.Language`.
- **Cubrilo con evals** (sección 12): cambio de idioma a mitad de la conversación, mensaje en inglés en un negocio con datos en español y viceversa.

### 7.4 Ejecución del agente (`AgentService`)

Por cada mensaje del usuario:
1. Validar el input: largo máximo (por ejemplo 1.000 caracteres) y sesión activa. Ver rate limit en la sección 9.
2. Crear el `AgentRun`.
3. Armar el contexto: system prompt del negocio, reglas comunes, últimos N mensajes (por ejemplo 20) e instrucción de idioma.
4. Llamar al `IChatClient` con streaming y las tools. Límites: `MaximumIterationsPerRequest` de la function invocation (por ejemplo 5) y `MaxOutputTokens`.
5. Transmitir al cliente por SSE los tokens y los eventos de traza (sección 10).
6. Persistir el mensaje del asistente y cerrar el run con tokens, costo y duración.
7. Ante cualquier error del proveedor: registrar un `TraceEvent` de tipo `Error`, cerrar el run en `Error` y mandar un evento `error` con mensaje amigable. Nunca dejar el stream colgado.

**Razón de diseño para documentar:** el flujo de la conversación lo controla el modelo, pero las reglas que no pueden fallar viven en código: scoping por negocio, idempotencia, cálculo de totales, capacidad de slots y límite de iteraciones. El modelo interpreta texto libre y decide qué tool usar; no calcula precios ni decide si hay lugar.

### 7.5 Traza

- **LLM:** un `DelegatingChatClient` propio (por ejemplo `TracingChatClient`), ubicado en el pipeline de forma que vea cada llamada individual al modelo, registra un `LlmCall` con modelo, tokens (de `Usage`, o del `UsageContent` en streaming), duración y, truncados, los últimos mensajes de entrada y la salida.
- **Tools:** registrar cada invocación como `ToolCall`, con argumentos, resultado y duración. Usá el hook de la function invocation de MEAI o envolvé cada `AIFunction` en un decorador. Verificá cuál ofrece hoy la librería.
- **Guardrails:** cuando se rechaza algo (input largo, iteraciones agotadas, intento de reservar en un slot lleno), registrar un evento `Guardrail`.
- **Datos personales:** truncá los payloads (por ejemplo a 4 KB) y enmascará el teléfono en la traza que ve el público (`+54 11 ****-4444`). En la tabla `Booking` se guarda completo.
- **Costo:** `tokens × ModelPricing`, a nivel evento y agregado en el run.

## 8. Proveedor de LLM

- **Configuración:** `Llm:Primary` y `Llm:Fallback`, cada uno con `BaseUrl`, `ApiKey` y `Model`. Se construyen con el `ChatClient` de OpenAI apuntando al `BaseUrl` y `.AsIChatClient()`.
- **Fallback:** si el primario responde 429 o 5xx **antes del primer token**, se reintenta una vez con el secundario y se registra en la traza qué proveedor respondió. La v1 ya lo hacía en TypeScript (`app/api/chat/route.ts`); portá la lógica.
- **Modelos:** verificá en la documentación de Gemini y de Groq qué modelos del free tier soportan function calling y elegí el default. El nombre del modelo es configuración, no código.
- **Pruebas:** antes de construir encima, probá con un script o test manual que el endpoint compatible con OpenAI del proveedor elegido soporta tool calling **en streaming** vía MEAI. Si no funciona, documentalo y usá el modo sin streaming para las iteraciones con tools, transmitiendo solo la respuesta final.

## 9. Seguridad y límites

- **Rate limiting en el servidor** (`Microsoft.AspNetCore.RateLimiting`): por IP (por ejemplo 30 mensajes cada 10 minutos) y por sesión (por ejemplo 25 mensajes en total; después, el banner "Restart demo").
- **Topes de costo:** `MaxOutputTokens`, máximo de iteraciones de tools y un tope diario global de requests configurable. Si se supera, la API responde un mensaje claro y la UI lo muestra.
- **CORS** restringido al origen del front.
- **Secrets** solo en variables de entorno. `.env.example` sin valores reales y `.gitignore` correcto.
- **Prompt injection:** el system prompt prohíbe revelar instrucciones, pero la protección real está en el código. Las tools no exponen nada fuera del negocio de la sesión y no hay ninguna tool peligrosa. Agregá evals de inyección.
- **`/admin`** protegido con una clave en un header (`X-Admin-Key`, por variable de entorno). Es una demo y el README lo aclara. En la demo pública, mostrar los datos de ejemplo con los teléfonos enmascarados.

## 10. Contrato de la API

| Método | Ruta | Descripción |
|---|---|---|
| `GET` | `/api/niches` | Rubros disponibles: id, nombre, persona, avatar, greeting y quick replies |
| `POST` | `/api/sessions` | Body `{ niche }` → `{ sessionId, greeting, quickReplies }` |
| `POST` | `/api/sessions/{id}/messages` | Body `{ text }` → **stream SSE** (eventos abajo) |
| `GET` | `/api/sessions/{id}/trace` | Runs y eventos de la sesión (para recargar el panel) |
| `GET` | `/api/admin/bookings?niche=` | Requiere `X-Admin-Key` |
| `GET` | `/api/admin/handoffs?niche=` | Requiere `X-Admin-Key` |
| `GET` | `/health` | Liveness, y readiness con chequeo de la base |

**Eventos SSE** (`event:` + `data:` JSON):

| Evento | Data |
|---|---|
| `run_started` | `{ runId }` |
| `token` | `{ text }` |
| `trace` | Un `TraceEvent` serializado: `{ sequence, type, name, input, output, tokensIn, tokensOut, durationMs, costUsd }` |
| `handoff` | `{ reason }` |
| `run_completed` | `{ runId, tokensIn, tokensOut, costUsd, durationMs, model, provider }` |
| `error` | `{ message, retryable }` |

Usá `fetch` con `ReadableStream` en el front, porque `EventSource` no soporta POST. Documentá el contrato en `docs/architecture.md`, con ejemplos.

## 11. Frontend

- **Portar la UI de la v1** a componentes React: header con avatar y estado, burbujas, quick replies, input, banners de sistema y formato WhatsApp (`*bold*`, `_italic_`). Tiene que verse igual o mejor que la v1, también en mobile.
- **Selector de rubro** arriba, como en la v1. Cambiar de rubro crea una sesión nueva.
- **Panel "Show agent trace":** toggle en el header. En desktop es un drawer lateral; en mobile, una hoja inferior. Para cada mensaje del usuario muestra un timeline con:
  - cada `LlmCall` (modelo, tokens in/out, ms);
  - cada `ToolCall` (nombre, argumentos y resultado en JSON colapsable, ms);
  - eventos de guardrail y handoff;
  - totales del run (tokens, costo en USD con 4 decimales, duración, proveedor).

  Se actualiza en vivo con los eventos SSE. Es la pantalla más importante para el portfolio: tiene que ser clara para alguien que nunca vio un agente.
- **`/admin`:** dos tablas de solo lectura, reservas y derivaciones, filtrables por rubro, con un link a la traza de la sesión.
- **Footer:** "Built by Agustín Rojas · GitHub · LinkedIn" (`https://github.com/agustinrojas1`, `https://www.linkedin.com/in/agustin-jorge-rojas/`). Sin CTA comercial.
- **Accesibilidad básica:** foco visible, `aria-live` en los mensajes nuevos y contraste suficiente.
- **Tests con Vitest** de lo que tiene lógica: parser del stream SSE, formato WhatsApp y reducer del estado del chat.

## 12. Tests y evals

**Tests (en CI, deterministas, sin red):**
- **Unit:**
  - cálculo de totales de pedidos;
  - idempotencia de `create_booking`;
  - capacidad de slots, incluida la concurrencia: dos reservas simultáneas por el último lugar, y solo una gana;
  - detección de idioma;
  - enmascarado de datos personales;
  - cálculo de costo.
- **Integración** (`WebApplicationFactory` + Testcontainers PostgreSQL + `IChatClient` falso con tool calls guionadas):
  - el endpoint de mensajes transmite tokens y trazas;
  - las tools no ven datos de otro negocio;
  - el rate limit corta;
  - un error del proveedor cierra el run en `Error` sin colgar el stream;
  - el fallback al segundo proveedor funciona.

**Evals (contra el modelo real, se corren a mano: `dotnet run --project evals/Chatbot.Evals`):**
- Casos en `evals/cases/*.yaml`. Cada caso tiene: rubro, conversación (turnos del usuario) y expectativas:
  - `expectToolCalls` (nombres y, opcionalmente, argumentos parciales);
  - `forbidToolCalls`;
  - `expectLanguage`;
  - `forbidPhrases` (por ejemplo "confirmed", "confirmada", "I've sent you an email");
  - `mustMention` (por ejemplo un precio que venga de la tool).
- **Mínimo 25 casos**, que cubran:
  - idioma: inglés, español y cambio de idioma a mitad de la conversación (el bug de la v1);
  - búsqueda con filtros;
  - disponibilidad;
  - reserva completa con confirmación;
  - intento de reservar sin datos;
  - pedido con total;
  - producto inexistente (no inventar);
  - pedido de una persona (handoff);
  - small talk (sin handoff);
  - prompt injection (no revelar el prompt; no operar sobre otro negocio);
  - mensaje incomprensible.
- **Salida:** tabla en consola y `evals/results/<fecha>.md` con tasa de aprobación por categoría, y tokens y costo totales.
- **Objetivo para el MVP:**
  - 100 % en idioma y en frases prohibidas;
  - 90 % o más en uso correcto de tools.

  Documentá en el README los resultados del último run y con qué modelo.

## 13. Deploy

- **Local:** `docker compose up` levanta postgres, api (con migraciones y seed al arrancar en Development) y web. Sin configuración manual aparte de copiar `.env.example` a `.env` y poner una API key.
- **Producción** (proveedores a confirmar con el dueño, sección 17):
  - **API:** contenedor en un servicio con free tier (Render, Azure Container Apps o Fly.io).
  - **Base:** PostgreSQL gestionado gratis (Neon o Supabase).
  - **Front:** Vercel o Netlify.

  Variables de entorno documentadas en el README. Las migraciones en producción se aplican con un paso explícito, no al arrancar.
- **CI** con GitHub Actions: build y tests de la API (con Testcontainers) y build, lint y tests del front, en cada push y PR.

## 14. README (es lo primero que mira un recruiter)

Debe incluir, en este orden:
1. Una línea de qué es, link a la demo y **GIF de 20–30 s** mostrando una reserva completa con el panel de traza abierto.
2. "What this demonstrates": bullets con tool calling real, traza y costo por turno, human-in-the-loop, idempotencia, rate limiting y evals.
3. Diagrama de arquitectura (el mermaid de la sección 5, actualizado).
4. Stack.
5. Cómo correrlo local (3 comandos).
6. Decisiones clave, con link a `docs/decisions.md`: por qué no RAG, por qué las reglas críticas viven en código, por qué SSE, cómo funciona el fallback de proveedor.
7. Resultados de los evals.
8. Limitaciones conocidas y próximos pasos.
9. Autor.

## 15. Milestones

| # | Entrega | Criterios de aceptación |
|---|---|---|
| **M0** | Esqueleto | Monorepo con la estructura de la sección 5; solución .NET y app Vite que compilan; `docker compose up` levanta postgres, api (`/health` OK) y web (página vacía); CI corriendo; `.env.example`; `docs/` creado |
| **M1** | Datos | Entidades, migración inicial y seed de los 3 negocios, con catálogo portado de la v1 y slots de 14 días; tests de las invariantes de la base (idempotencia, capacidad) |
| **M2** | Agente sin UI | Cliente LLM configurable con fallback; las 5 tools; `AgentService`; endpoint SSE; detección de idioma. Probado con `curl` contra el modelo real: una reserva queda en la base. Tests de integración con `IChatClient` falso |
| **M3** | Traza | `TracingChatClient`, traza de tools y guardrails, costo, eventos `trace` por SSE y endpoint `/trace`; enmascarado de datos personales. Tests |
| **M4** | Frontend chat | UI WhatsApp portada, selector de rubro, streaming, banners (handoff, rate limit, error) y footer de autoría. Probado en desktop y mobile |
| **M5** | Panel de traza y admin | Drawer "Show agent trace" en vivo y `/admin`. Un visitante puede seguir una reserva paso a paso |
| **M6** | Seguridad y evals | Rate limit, topes, CORS, evals con al menos 25 casos y objetivos cumplidos; resultados en el README |
| **M7** | Deploy y README | Demo pública funcionando, CI en verde, README completo con GIF, `docs/decisions.md` con al menos 6 ADRs |

## 16. Definition of done (de todo el proyecto)

- Un visitante entra a la demo, elige un rubro, reserva un turno en inglés o en español y ve en el panel cada paso, con tokens y costo. La reserva aparece en `/admin`.
- Pedir una persona crea una derivación visible en `/admin`.
- Ninguna respuesta dice "confirmado" ni inventa productos (verificado por evals).
- `docker compose up` funciona en una máquina limpia, y los tests y la CI están en verde.
- El README permite entender el proyecto en 2 minutos y correrlo en 5.

## 17. Decisiones abiertas (preguntar al dueño antes de resolverlas)

1. **Hosting de producción:** proveedor de la API, de la base y del front.
2. **Proveedor y modelo de LLM por defecto:** el dueño tiene una key de Gemini (free tier) y puede crear una de Groq. Confirmar con qué key se hace el deploy.
3. **Nombre del repo y del proyecto** (sugerencia: `ai-booking-agent-dotnet`).
4. **Clave de admin en la demo pública:** si `/admin` queda abierto en modo solo lectura con datos enmascarados, o protegido.
5. **Licencia** (sugerencia: MIT).

## 18. Referencias

- v1: <https://github.com/agustinrojas1/whatsapp-ai-chatbot-demo> (UI en `app/page.tsx`, personas y fallback en `app/api/chat/route.ts`)
- `IChatClient`: <https://learn.microsoft.com/en-us/dotnet/ai/ichatclient>
- Microsoft.Extensions.AI.OpenAI (tool calling, DI, minimal API): <https://github.com/dotnet/extensions/blob/main/src/Libraries/Microsoft.Extensions.AI.OpenAI/README.md>
- `UseFunctionInvocation`: <https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.functioninvokingchatclientbuilderextensions.usefunctioninvocation>
- Gemini, compatibilidad con OpenAI: <https://ai.google.dev/gemini-api/docs/openai>
- Rate limiting en ASP.NET Core: <https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit>
- Testcontainers for .NET: <https://dotnet.testcontainers.org/>
