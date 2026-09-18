using CvHub.Domain;
using CvHub.Shared;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<AttributeDef> Attributes => Set<AttributeDef>();
    public DbSet<AttributeValue> AttributeValues => Set<AttributeValue>();
    public DbSet<ProfileAttribute> ProfileAttributes => Set<ProfileAttribute>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ProjectTag> ProjectTags => Set<ProjectTag>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<PositionAttribute> PositionAttributes => Set<PositionAttribute>();
    public DbSet<PositionFilter> PositionFilters => Set<PositionFilter>();
    public DbSet<PositionTag> PositionTags => Set<PositionTag>();
    public DbSet<Cv> Cvs => Set<Cv>();
    public DbSet<CvProject> CvProjects => Set<CvProject>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<CvLike> Likes => Set<CvLike>();
    public DbSet<RecentAttribute> RecentAttributes => Set<RecentAttribute>();
    public DbSet<CvAttributeVersion> CvAttributeVersions => Set<CvAttributeVersion>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // ---- Optimistic locking on PostgreSQL xmin system column ----
        b.Entity<AttributeDef>().Property<uint>("xmin").IsRowVersion();
        b.Entity<AttributeValue>().Property<uint>("xmin").IsRowVersion();
        b.Entity<Position>().Property<uint>("xmin").IsRowVersion();
        b.Entity<Project>().Property<uint>("xmin").IsRowVersion();

        // ---- Uniqueness ----
        b.Entity<AttributeDef>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Tag>().HasIndex(x => x.Name).IsUnique();
        b.Entity<AttributeValue>().HasIndex(x => new { x.UserId, x.AttributeId }).IsUnique();
        b.Entity<ProfileAttribute>().HasIndex(x => new { x.UserId, x.AttributeId }).IsUnique();
        b.Entity<Cv>().HasIndex(x => new { x.PositionId, x.UserId }).IsUnique();
        b.Entity<CvLike>().HasIndex(x => new { x.CvId, x.UserId }).IsUnique();
        b.Entity<ProjectTag>().HasIndex(x => new { x.ProjectId, x.TagId }).IsUnique();
        b.Entity<PositionTag>().HasIndex(x => new { x.PositionId, x.TagId }).IsUnique();
        b.Entity<PositionAttribute>().HasIndex(x => new { x.PositionId, x.AttributeId }).IsUnique();

        // ---- Query filters (soft deletes) ----
        b.Entity<AttributeDef>().HasQueryFilter(a => !a.IsDeleted);
        b.Entity<Position>().HasQueryFilter(p => !p.IsDeleted);
        b.Entity<Cv>().HasQueryFilter(c => !c.IsDeleted);

        // ---- Full-text search: tsvector generated columns + GIN indexes are added via raw SQL in the migration ----

        // ---- Indexes for common queries ----
        b.Entity<Position>().HasIndex(p => new { p.UpdatedAt });
        b.Entity<Position>().HasIndex(p => new { p.Level });
        b.Entity<Cv>().HasIndex(c => c.Status);
        b.Entity<Comment>().HasIndex(c => new { c.PositionId, c.CreatedAt });
        b.Entity<CvLike>().HasIndex(l => l.CvId);
        b.Entity<AttributeValue>().HasIndex(v => v.UserId);

        // ---- CvAttributeVersion ----
        b.Entity<CvAttributeVersion>().HasIndex(x => new { x.PositionId, x.AttributeId }).IsUnique();
    }
}
