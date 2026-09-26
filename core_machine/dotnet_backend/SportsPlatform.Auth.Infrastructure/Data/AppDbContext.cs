using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Infrastructure.Data.Configurations;

namespace SportsPlatform.Auth.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserAuthProvider> UserAuthProviders => Set<UserAuthProvider>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Club> Clubs => Set<Club>();
    public DbSet<ClubMembership> ClubMemberships => Set<ClubMembership>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMembership> TeamMemberships => Set<TeamMembership>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<PlayerProfile> PlayerProfiles => Set<PlayerProfile>();
    public DbSet<PlayerTeam> PlayerTeams => Set<PlayerTeam>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<EventException> EventExceptions => Set<EventException>();
    public DbSet<Attendance> Attendances => Set<Attendance>();
    public DbSet<MedicalRecord> MedicalRecords => Set<MedicalRecord>();
    public DbSet<MedicalDocumentRequest> MedicalDocumentRequests => Set<MedicalDocumentRequest>();
    public DbSet<FitnessRecord> FitnessRecords => Set<FitnessRecord>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<CoachingPlan> CoachingPlans => Set<CoachingPlan>();
    public DbSet<CoachingLineup> CoachingLineups => Set<CoachingLineup>();
    public DbSet<CoachingLineupPlayer> CoachingLineupPlayers => Set<CoachingLineupPlayer>();
    public DbSet<CoachingPlanDocument> CoachingPlanDocuments => Set<CoachingPlanDocument>();
    public DbSet<PlayerGameStats> PlayerGameStats => Set<PlayerGameStats>();
    public DbSet<MatchStats> MatchStats => Set<MatchStats>();
    public DbSet<MatchStatsDocument> MatchStatsDocuments => Set<MatchStatsDocument>();
    public DbSet<PlayerMatchStats> PlayerMatchStats => Set<PlayerMatchStats>();
    public DbSet<MatchAnalysisReport> MatchAnalysisReports => Set<MatchAnalysisReport>();
    public DbSet<MatchLineupAnalysis> MatchLineupAnalyses => Set<MatchLineupAnalysis>();
    public DbSet<MatchAnalysisDocument> MatchAnalysisDocuments => Set<MatchAnalysisDocument>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationParticipant> ConversationParticipants => Set<ConversationParticipant>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();
    public DbSet<MessageReadReceipt> MessageReadReceipts => Set<MessageReadReceipt>();
    public DbSet<AppNotification> AppNotifications => Set<AppNotification>();
    public DbSet<EventDocument> EventDocuments => Set<EventDocument>();
    public DbSet<EventPlan> EventPlans => Set<EventPlan>();
    public DbSet<CoachNote> CoachNotes => Set<CoachNote>();
    public DbSet<GameVideo> GameVideos => Set<GameVideo>();
    public DbSet<PlayerVideo> PlayerVideos => Set<PlayerVideo>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Map PostgreSQL enums
        modelBuilder.HasPostgresEnum<AuthProviderType>("public", "auth_provider_type");
        modelBuilder.HasPostgresEnum<RoleNameType>("public", "role_name_type");
        modelBuilder.HasPostgresEnum<InvitationStatus>("public", "invitation_status");
        modelBuilder.HasPostgresEnum<MembershipStatus>("public", "membership_status");
        modelBuilder.HasPostgresEnum<EventType>("public", "event_type");
        modelBuilder.HasPostgresEnum<AttendanceStatus>("public", "attendance_status");
        modelBuilder.HasPostgresEnum<AnnouncementPriority>("public", "announcement_priority");
        modelBuilder.HasPostgresEnum<PlanVisibility>("public", "plan_visibility");

        // Apply entity configurations
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new UserAuthProviderConfiguration());
        modelBuilder.ApplyConfiguration(new RefreshTokenConfiguration());
        modelBuilder.ApplyConfiguration(new ClubConfiguration());
        modelBuilder.ApplyConfiguration(new ClubMembershipConfiguration());
        modelBuilder.ApplyConfiguration(new TeamConfiguration());
        modelBuilder.ApplyConfiguration(new TeamMembershipConfiguration());
        modelBuilder.ApplyConfiguration(new InvitationConfiguration());
        modelBuilder.ApplyConfiguration(new PlayerProfileConfiguration());
        modelBuilder.ApplyConfiguration(new PlayerTeamConfiguration());
        modelBuilder.ApplyConfiguration(new SeasonConfiguration());
        modelBuilder.ApplyConfiguration(new EventConfiguration());
        modelBuilder.ApplyConfiguration(new EventExceptionConfiguration());
        modelBuilder.ApplyConfiguration(new AttendanceConfiguration());
        modelBuilder.ApplyConfiguration(new MedicalRecordConfiguration());
        modelBuilder.ApplyConfiguration(new MedicalDocumentRequestConfiguration());
        modelBuilder.ApplyConfiguration(new FitnessRecordConfiguration());
        modelBuilder.ApplyConfiguration(new AnnouncementConfiguration());
        modelBuilder.ApplyConfiguration(new CoachingPlanConfiguration());
        modelBuilder.ApplyConfiguration(new CoachingLineupConfiguration());
        modelBuilder.ApplyConfiguration(new CoachingLineupPlayerConfiguration());
        modelBuilder.ApplyConfiguration(new CoachingPlanDocumentConfiguration());
        modelBuilder.ApplyConfiguration(new PlayerGameStatsConfiguration());
        modelBuilder.ApplyConfiguration(new MatchStatsConfiguration());
        modelBuilder.ApplyConfiguration(new MatchStatsDocumentConfiguration());
        modelBuilder.ApplyConfiguration(new PlayerMatchStatsConfiguration());
        modelBuilder.ApplyConfiguration(new MatchAnalysisReportConfiguration());
        modelBuilder.ApplyConfiguration(new MatchLineupAnalysisConfiguration());
        modelBuilder.ApplyConfiguration(new MatchAnalysisDocumentConfiguration());
        modelBuilder.ApplyConfiguration(new ConversationConfiguration());
        modelBuilder.ApplyConfiguration(new ConversationParticipantConfiguration());
        modelBuilder.ApplyConfiguration(new MessageConfiguration());
        modelBuilder.ApplyConfiguration(new MessageReactionConfiguration());
        modelBuilder.ApplyConfiguration(new MessageReadReceiptConfiguration());
        modelBuilder.ApplyConfiguration(new AppNotificationConfiguration());
        modelBuilder.ApplyConfiguration(new EventDocumentConfiguration());
        modelBuilder.ApplyConfiguration(new EventPlanConfiguration());
        modelBuilder.ApplyConfiguration(new CoachNoteConfiguration());
        modelBuilder.ApplyConfiguration(new GameVideoConfiguration());
        modelBuilder.ApplyConfiguration(new PlayerVideoConfiguration());
    }
}
