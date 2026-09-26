using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class ClubConfiguration : IEntityTypeConfiguration<Club>
{
    public void Configure(EntityTypeBuilder<Club> builder)
    {
        builder.ToTable("club");
        builder.HasKey(c => c.ClubId);
        builder.Property(c => c.ClubId).HasColumnName("club_id");
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(c => c.LogoUrl).HasColumnName("logo_url").HasMaxLength(500);
        builder.Property(c => c.Location).HasColumnName("location").HasMaxLength(200);
        builder.Property(c => c.LocationLatitude).HasColumnName("location_latitude");
        builder.Property(c => c.LocationLongitude).HasColumnName("location_longitude");
        builder.Property(c => c.CreatedBy).HasColumnName("created_by");
        builder.Property(c => c.DeletedAt).HasColumnName("deleted_at");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(c => c.CreatedBy).IsUnique();

        builder.HasOne(c => c.Creator)
               .WithOne(u => u.CreatedClub)
               .HasForeignKey<Club>(c => c.CreatedBy)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(c => c.DeletedAt == null);
    }
}
