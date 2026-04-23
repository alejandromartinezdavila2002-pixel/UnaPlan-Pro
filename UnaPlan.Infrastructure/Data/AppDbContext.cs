using Microsoft.EntityFrameworkCore;
using UnaPlan.Core.Entities;

namespace UnaPlan.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<CalendarioProcesado> Calendarios { get; set; }
    public DbSet<MateriaEvaluacion> Evaluaciones { get; set; }

    // Agregamos nuestras dos nuevas tablas maestras
    public DbSet<PlanDeCurso> PlanesDeCurso { get; set; }
    public DbSet<MaterialApoyo> MaterialesDeApoyo { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 1. Configurar el guardado JSON
        modelBuilder.Entity<CalendarioProcesado>()
            .Property(c => c.DatosJsonRaw)
            .HasColumnType("jsonb");

        // 2. Configurar PlanDeCurso
        // Le decimos explícitamente que CodigoMateria es la llave primaria, no un ID normal.
        modelBuilder.Entity<PlanDeCurso>()
            .HasKey(p => p.CodigoMateria);

        // 3. Relación 1 a Muchos (PlanDeCurso -> MaterialApoyo)
        modelBuilder.Entity<PlanDeCurso>()
            .HasMany(p => p.MaterialesDeApoyo)
            .WithOne()
            .HasForeignKey(m => m.CodigoMateria)
            // Si borramos la materia, se borran sus links de apoyo automáticamente
            .OnDelete(DeleteBehavior.Cascade);
    }
}