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

    public DbSet<PlanDeCurso> PlanesDeCurso { get; set; }
    public DbSet<MaterialApoyo> MaterialesDeApoyo { get; set; }

    public DbSet<TrabajosPublicados> TrabajosPublicados { get; set; }
    public DbSet<EstudiantesSuscritos> EstudiantesSuscritos { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CalendarioProcesado>()
            .Property(c => c.DatosJsonRaw)
            .HasColumnType("jsonb");

        // CodigoMateria es la llave primaria de PlanDeCurso, no un ID normal
        modelBuilder.Entity<PlanDeCurso>()
            .HasKey(p => p.CodigoMateria);

        // PlanDeCurso -> MaterialApoyo, 1 a muchos
        modelBuilder.Entity<PlanDeCurso>()
            .HasMany(p => p.MaterialesDeApoyo)
            .WithOne()
            .HasForeignKey(m => m.CodigoMateria)
            // Si borramos la materia, se borran sus links de apoyo automáticamente
            .OnDelete(DeleteBehavior.Cascade);


        // MateriaEvaluacion <-> TrabajosPublicados, 1 a 1
        modelBuilder.Entity<MateriaEvaluacion>()
            .HasOne(e => e.TrabajoPublicado)
            .WithOne(t => t.Evaluacion)
            .HasForeignKey<TrabajosPublicados>(t => t.MateriaEvaluacionId)
            .OnDelete(DeleteBehavior.Cascade); // Si borras la evaluación de la DB, se borra también su link de Drive

        modelBuilder.Entity<EstudiantesSuscritos>()
            .Property(e => e.MateriasInscritas)
            .HasColumnType("text[]");
    }
}
