# Plan: Panel "Periodo Académico" en Telegram

Rama: `feature/panel-periodo-academico` · Estado: **implementado y compilando — falta probar en Telegram y desplegar**

## Objetivo

Iniciar un nuevo periodo académico completo desde Telegram con un solo PDF:
PIN → PDF del calendario → resumen → confirmar → limpiar + cargar → convocatoria masiva.
Además, generar el plan de evaluación de un estudiante (`solicitar-plan`) desde el mismo panel.

Sin tablas nuevas en Supabase: el PIN vive en una variable de entorno.

## Configuración

| Dónde | Clave | Estado |
|---|---|---|
| Local | `dotnet user-secrets set "Telegram:PinPeriodoAcademico" "<pin>" --project UnaPlan.Api` | ✅ Configurado |
| Render | `Telegram__PinPeriodoAcademico` (el `__` equivale a `:` en .NET) | ⏳ Pendiente (manual) |

- Sin la variable, el panel responde "PIN no configurado" y no ejecuta nada (falla cerrado).
- El valor del PIN **no** se escribe en el repositorio.

## Hallazgos que motivan el diseño

1. `/api/calendarios/confirm` recibe el JSON de `/preview`, no el PDF: el flujo real es extraer → limpiar → guardar.
2. Limpiar **antes** de validar el PDF podía dejar el semestre vacío si el PDF no se leía bien.
3. La lógica vivía en lambdas de `Program.cs`; el bot no podía reutilizarla sin llamarse a sí mismo por HTTP.
4. `clear-all` eran 3 sentencias sin transacción.
5. `MateriaEvaluacion.CodigoMateria` es FK a `PlanesDeCurso`: un código fuera del catálogo puede tumbar el guardado.
6. Telegram reintenta el mismo update si no recibe 200 a tiempo → riesgo de ejecutar dos veces.
7. Los callbacks inline no verificaban que quien pulsa sea admin.

## Fases

### Fase 1 — Lógica en servicios (los endpoints quedan como envoltorios) ✅
- [x] `PdfExtractorService.ProcesarCalendarioPdf(Stream, nombre)` (sobrecarga; la de ruta la reutiliza).
- [x] `PeriodoAcademicoService`: `ExtraerCalendario`, `ObtenerCodigosFueraDeCatalogoAsync`,
      `ReiniciarSemestreAsync`, `GuardarCalendarioAsync`, `ReiniciarYCargarAsync`, `EnviarConvocatoriaAsync`.
- [x] `PlanEstudianteService.GenerarYEnviarAsync` (lógica de `solicitar-plan`).
- [x] Endpoints `preview`, `confirm`, `clear-all`, `convocatoria-semestre`, `solicitar-plan` llaman a los servicios.

### Fase 2 — Reinicio seguro ✅
- [x] Limpiar + guardar en **una sola transacción**: si algo falla, se revierte todo.
- [x] Borrado explícito de `Evaluaciones` (no depender de un `ON DELETE CASCADE` que puede no existir).
- [x] Evaluaciones con código fuera del catálogo se omiten y se reportan.
- [x] Candado (`SemaphoreSlim`) para que no corran dos reinicios ni dos convocatorias a la vez.

### Fase 3 — Infraestructura del bot ✅ (`TelegramControlService` + webhook)
- [x] Sesión por `chatId` en memoria (expira a los 10 min sin actividad; se pierde si Render reinicia).
- [x] Recepción de documentos PDF (≤ 20 MB, límite de la Bot API) descargados a memoria.
- [x] Callbacks con prefijo `pa:` + verificación de admin + `AnswerCallbackQuery` + se quitan los botones pulsados.
- [x] Deduplicación por `update_id` en el webhook.
- [x] Trabajo pesado en segundo plano (`Task.Run`) para responder 200 rápido.
- [x] `/cancelar` o el botón 🎓 cortan cualquier flujo abierto (salvo uno que ya se está ejecutando).

### Fase 4 — Flujo "Iniciar nuevo periodo" ✅
```
[🎓 Periodo Académico] → 🚀 Iniciar nuevo periodo | 📝 Generar plan a estudiante | ❌ Cerrar
🚀 → PIN (se borra el mensaje; 3 intentos fallidos = bloqueo 15 min)
   → PDF → Resumen (materias, evaluaciones TP/TSP, rango de fechas, códigos omitidos, estudiantes afectados)
   → [✅ Confirmar reinicio] [❌ Cancelar]   (doble toque = una sola ejecución)
   → Transacción limpiar + cargar → Convocatoria en segundo plano → Reporte final
```
- PDF sin evaluaciones o sin ningún código del catálogo → no se borra nada y vuelve a pedir el PDF.
- Si la carga falla → se informa que todo se revirtió. Si falla solo la convocatoria → el periodo queda cargado
  y se puede lanzar desde **📣 Estado de la convocatoria** (solo a quien no la recibió).

### Fase 4B — Convocatoria por lotes que sigue al día siguiente ✅
- [x] Columna `EstudiantesSuscritos.FechaConvocatoria` (`sql/2026-10-14_fecha_convocatoria.sql`).
      Pendiente = materias vacías **y** `FechaConvocatoria` nula. El reinicio de semestre la pone en null.
- [x] Cada envío exitoso se marca al instante → relanzar nunca duplica correos (ni tras un reinicio de Render).
- [x] Tope propio: **350 por ventana móvil de 24 h** (`Crm:LimiteDiarioConvocatoria`), contado desde la BD.
      Deja ~100 de los 450 del guardián para alertas TP/TSP y planes.
- [x] Se detiene el lote si Gmail responde cuota agotada (429/403), si corta el guardián o tras 5 fallos seguidos.
      Un correo que falla no se reintenta durante 24 h.
- [x] `ConvocatoriaWorkerService`: cada hora, de 7:00 a 22:00 (Venezuela), reanuda si hay una convocatoria
      del periodo actual a medio enviar y hay cupo. Avisa por Telegram solo si hubo avance.
      Un `clear-all` sin convocatoria o estudiantes viejos con materias vacías **no** disparan envíos.
- [x] Panel: **📣 Estado de la convocatoria** (pendientes, usados en 24 h, cupo, próximo cupo) + "Enviar ahora".
- [x] `POST /api/admin/crm/convocatoria-semestre` usa el mismo lote y devuelve motivo, pendientes y cupo.

### Fase 5 — Flujo "Generar plan a estudiante" ✅
- [x] Nombre → correo (validado) → códigos → resumen → confirmar → Excel + correo + suscripción.
- Sin PIN: no es destructivo y el bot ya es solo para admins.

### Fase 6 — Pruebas
- [x] `dotnet build` sin errores.
- [ ] Swagger: los endpoints responden igual que antes.
- [ ] Telegram con el PDF del semestre pasado:
  - [ ] PIN incorrecto (y bloqueo al 3er intento)
  - [ ] Archivo que no es PDF / PDF con 0 evaluaciones
  - [ ] Cancelar a mitad de flujo
  - [ ] Doble toque en "Confirmar"
  - [ ] Convocatoria con tope bajo (p. ej. `Crm__LimiteDiarioConvocatoria=2`): corta, "Estado" muestra
        pendientes y próximo cupo, y relanzar no reenvía a quien ya la recibió
  - [ ] Generar plan a estudiante con un código inexistente

### Despliegue (en este orden)
1. **Supabase → SQL Editor:** ejecutar `sql/2026-10-14_fecha_convocatoria.sql`.
   Va primero: sin la columna, el código nuevo falla en toda consulta a `EstudiantesSuscritos`.
2. En Render → Environment: agregar `Telegram__PinPeriodoAcademico`
   (opcional: `Crm__LimiteDiarioConvocatoria`, por defecto 350).
3. Merge de la rama y deploy.
4. Al arrancar, el bot reenvía el teclado con el nuevo botón **🎓 Periodo Académico**.

### Fase 7 (opcional) — Proteger endpoints admin
- [ ] API key por header para `/api/admin/*` y `/api/calendarios/*` (otra variable de entorno).
      Hoy son públicos y Swagger está activo en producción.

## Decisiones tomadas
1. Confirmación explícita tras el resumen (no automático al recibir el PDF).
2. Códigos fuera del catálogo: se guardan solo los válidos y se listan los omitidos.
3. Convocatoria por lotes: tope de 350 cada 24 h, marca por estudiante (`FechaConvocatoria`) y
   reanudación automática al liberarse cupo. Una columna nueva, ninguna tabla nueva.

## Límites conocidos
- El guardián global de `EmailService` (450/día) sigue en memoria: si Render reinicia, vuelve a 0.
  El tope de la convocatoria no depende de él (se cuenta desde la BD).
- Si un correo sale pero falla el guardado de su marca (caída de BD justo en ese instante),
  ese estudiante podría recibirla dos veces. Es una ventana de milisegundos.
- La reanudación depende de que Render esté despierto (el keep-alive actual lo mantiene).
