-- ============================================================================
-- UnaPlan · 2026-10-14 · Columna FechaConvocatoria en EstudiantesSuscritos
--
-- Marca cuándo cada estudiante recibió la convocatoria del periodo actual.
-- Permite cortar la convocatoria por el tope diario y reanudarla al día siguiente
-- sin reenviársela a nadie. El reinicio de semestre la vuelve a NULL.
--
-- ⚠️ Ejecutar en el SQL Editor de Supabase ANTES de desplegar el código nuevo:
--    EF Core selecciona todas las columnas de la entidad, así que sin esta columna
--    fallaría cualquier consulta a EstudiantesSuscritos (Notion, alertas, planes).
--    Es aditiva y nula: el código actual en producción la ignora sin problema.
-- ============================================================================

ALTER TABLE "EstudiantesSuscritos"
    ADD COLUMN IF NOT EXISTS "FechaConvocatoria" timestamp with time zone NULL;

-- Verificación: debe devolver dos filas, ambas 'timestamp with time zone'
SELECT column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_name = 'EstudiantesSuscritos'
  AND column_name IN ('FechaAdvertencia', 'FechaConvocatoria');
