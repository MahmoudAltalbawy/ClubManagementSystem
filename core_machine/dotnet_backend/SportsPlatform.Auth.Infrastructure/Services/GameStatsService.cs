using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class GameStatsService : IGameStatsService
{
    public const string StatsExtractorHttpClientName = "StatsExtractor";

    private readonly AppDbContext _db;
    private readonly INotificationService _notifications;
    private readonly IHttpClientFactory _httpClientFactory;

    public GameStatsService(
        AppDbContext db,
        INotificationService notifications,
        IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _notifications = notifications;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<MatchStatsDto> CreateStatsAsync(Guid clubId, Guid teamId, Guid callerUserId, CreateMatchStatsRequest request)
    {
        var team = await GetTeamAsync(clubId, teamId);
        await EnsureCanRecordAsync(team, callerUserId);

        var gameEvent = await _db.Events
            .FirstOrDefaultAsync(e => e.EventId == request.EventId && e.TeamId == teamId)
            ?? throw new InvalidOperationException("Event not found for this team.");

        if (gameEvent.EventType is not (EventType.Match or EventType.Training))
            throw new InvalidOperationException("Stats can only be recorded for match or training events.");

        var activePlayerIds = await _db.TeamMemberships
            .Where(tm => tm.TeamId == teamId && tm.Role == RoleNameType.Player && tm.Status == MembershipStatus.Active)
            .Select(tm => tm.UserId)
            .ToListAsync();

        var duplicatePlayers = request.PlayerStats.GroupBy(p => p.PlayerUserId).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicatePlayers.Count > 0)
            throw new InvalidOperationException("A player can only appear once in the stats entry.");

        var invalidPlayer = request.PlayerStats.FirstOrDefault(p => !activePlayerIds.Contains(p.PlayerUserId));
        if (invalidPlayer != null)
            throw new InvalidOperationException("All player stats must belong to active players on this team.");

        var existing = await _db.MatchStats
            .Include(s => s.PlayerStats)
            .FirstOrDefaultAsync(s => s.TeamId == teamId && s.EventId == request.EventId);

        var now = DateTime.UtcNow;
        MatchStats entity;
        if (existing == null)
        {
            entity = new MatchStats
            {
                MatchStatsId = Guid.NewGuid(),
                TeamId = teamId,
                EventId = gameEvent.EventId,
                SeasonId = gameEvent.SeasonId,
                RecordedBy = callerUserId,
                CreatedAt = now,
            };
            _db.MatchStats.Add(entity);
        }
        else
        {
            entity = existing;
            entity.RecordedBy = callerUserId;
            _db.PlayerMatchStats.RemoveRange(entity.PlayerStats);
        }

        ApplyTeamStats(entity, request, now);

        foreach (var player in request.PlayerStats)
        {
            entity.PlayerStats.Add(new PlayerMatchStats
            {
                PlayerMatchStatsId = Guid.NewGuid(),
                MatchStatsId = entity.MatchStatsId,
                TeamId = teamId,
                EventId = gameEvent.EventId,
                SeasonId = gameEvent.SeasonId,
                PlayerUserId = player.PlayerUserId,
                MinutesPlayed = player.MinutesPlayed,
                Goals = player.Goals,
                Assists = player.Assists,
                ShotsOnTarget = player.ShotsOnTarget,
                TotalShots = player.TotalShots,
                PassesCompleted = player.PassesCompleted,
                PassesAttempted = player.PassesAttempted,
                PassAccuracy = NormalizeAccuracy(player.PassAccuracy, player.PassesCompleted, player.PassesAttempted),
                Tackles = player.Tackles,
                Interceptions = player.Interceptions,
                YellowCards = player.YellowCards,
                RedCards = player.RedCards,
                Rating = player.Rating,
                Notes = Clean(player.Notes),
            });
        }

        await _db.SaveChangesAsync();
        await NotifyStatsSavedAsync(team, callerUserId, gameEvent.EventId, gameEvent.Title, entity.MatchStatsId);
        return await BuildMatchStatsDtoAsync(entity.MatchStatsId, callerUserId);
    }

    public async Task<StatsUploadPreviewDto> PreviewUploadAsync(Guid clubId, Guid teamId, Guid callerUserId, Guid eventId, string fileName, Stream fileContent)
    {
        var team = await GetTeamAsync(clubId, teamId);
        await EnsureCanRecordAsync(team, callerUserId);

        var gameEvent = await _db.Events.FirstOrDefaultAsync(e => e.EventId == eventId && e.TeamId == teamId)
            ?? throw new InvalidOperationException("Event not found for this team.");

        if (gameEvent.EventType is not (EventType.Match or EventType.Training))
            throw new InvalidOperationException("Stats can only be uploaded for match or training events.");

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        string text;
        if (extension == ".csv")
        {
            using var reader = new StreamReader(fileContent, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            text = await reader.ReadToEndAsync();
        }
        else if (extension == ".pdf")
        {
            text = await ExtractPdfTextAsync(fileName, fileContent);
        }
        else
        {
            throw new InvalidOperationException("Upload a CSV or PDF file.");
        }

        var parsed = await ParseUploadedStatsAsync(team, eventId, text, extension);
        return new StatsUploadPreviewDto
        {
            FileName = fileName,
            CanSave = parsed != null,
            Message = parsed == null ? "The file did not contain recognizable stats rows." : "Preview parsed successfully.",
            ParsedStats = parsed,
        };
    }

    public async Task<TeamStatsAggregateDto> GetTeamAggregatesAsync(Guid clubId, Guid teamId, Guid callerUserId)
    {
        var team = await GetTeamAsync(clubId, teamId);
        await EnsureCanViewTeamAsync(team, callerUserId);

        var stats = await _db.MatchStats
            .Include(s => s.Event)
            .Where(s => s.TeamId == teamId)
            .ToListAsync();

        var playerStats = await _db.PlayerMatchStats
            .Include(s => s.Player)
            .Where(s => s.TeamId == teamId)
            .ToListAsync();

        var canViewAllPlayers = await CanViewAllPlayerStatsAsync(team, callerUserId);

        return new TeamStatsAggregateDto
        {
            TotalEvents = stats.Count,
            Matches = stats.Count(s => s.Event.EventType == EventType.Match),
            Trainings = stats.Count(s => s.Event.EventType == EventType.Training),
            Wins = stats.Count(s => s.Result == "Win"),
            Draws = stats.Count(s => s.Result == "Draw"),
            Losses = stats.Count(s => s.Result == "Loss"),
            TotalGoals = stats.Sum(s => s.TotalGoals ?? 0),
            TotalAssists = stats.Sum(s => s.TotalAssists ?? 0),
            ShotsOnTarget = stats.Sum(s => s.ShotsOnTarget ?? 0),
            TotalShots = stats.Sum(s => s.TotalShots ?? 0),
            PassesCompleted = stats.Sum(s => s.PassesCompleted ?? 0),
            PassesAttempted = stats.Sum(s => s.PassesAttempted ?? 0),
            AveragePossessionPercent = AverageNullable(stats.Select(s => s.PossessionPercent)),
            AveragePassAccuracy = AverageNullable(stats.Select(s => s.PassAccuracy)),
            Tackles = stats.Sum(s => s.Tackles ?? 0),
            Interceptions = stats.Sum(s => s.Interceptions ?? 0),
            YellowCards = stats.Sum(s => s.YellowCards ?? 0),
            RedCards = stats.Sum(s => s.RedCards ?? 0),
            PlayerLeaderboard = canViewAllPlayers ? BuildPlayerAggregates(playerStats) : new List<PlayerStatsAggregateDto>(),
        };
    }

    public async Task<List<MatchStatsSummaryDto>> GetMatchHistoryAsync(Guid clubId, Guid teamId, Guid callerUserId)
    {
        var team = await GetTeamAsync(clubId, teamId);
        await EnsureCanViewTeamAsync(team, callerUserId);

        var history = await _db.MatchStats
            .Include(s => s.Event)
            .Include(s => s.PlayerStats)
            .Where(s => s.TeamId == teamId)
            .OrderByDescending(s => s.UpdatedAt)
            .ThenByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Event.StartAt)
            .Select(s => new MatchStatsSummaryDto
            {
                MatchStatsId = s.MatchStatsId,
                EventId = s.EventId,
                EventTitle = s.Event.Title,
                EventType = s.Event.EventType.ToString(),
                EventStartAt = s.Event.StartAt,
                OpponentName = s.OpponentName,
                TeamScore = s.TeamScore,
                OpponentScore = s.OpponentScore,
                Result = s.Result,
                Venue = s.Venue,
                CompetitionName = s.CompetitionName,
                Category = s.Category,
                GameNo = s.GameNo,
                Matchup = s.Matchup,
                TwoPtMA = s.TwoPtMA,
                ThreePtMA = s.ThreePtMA,
                FtMA = s.FtMA,
                OffensiveRebounds = s.OffensiveRebounds,
                DefensiveRebounds = s.DefensiveRebounds,
                TotalRebounds = s.TotalRebounds,
                BasketballAssists = s.BbAssists,
                Turnovers = s.Turnovers,
                Steals = s.Steals,
                Blocks = s.Blocks,
                PersonalFouls = s.PersonalFouls,
                FoulsDrawn = s.FoulsDrawn,
                Efficiency = s.Efficiency,
                Points = s.Points,
                Minutes = s.Minutes,
                PlayerCount = s.PlayerStats.Count,
                CreatedAt = s.CreatedAt,
                UpdatedAt = s.UpdatedAt,
            })
            .ToListAsync();

        foreach (var summary in history)
        {
            summary.OpponentName ??= ExtractOpponentName(summary.Matchup, team.TeamName);
        }

        return history;
    }

    public async Task<MatchStatsDto> GetMatchStatsAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId)
    {
        var team = await GetTeamAsync(clubId, teamId);
        await EnsureCanViewTeamAsync(team, callerUserId);

        var matchStats = await _db.MatchStats
            .FirstOrDefaultAsync(s => s.TeamId == teamId && s.EventId == eventId)
            ?? throw new InvalidOperationException("Stats not found for this event.");

        return await BuildMatchStatsDtoAsync(matchStats.MatchStatsId, callerUserId);
    }

    public async Task DeleteMatchStatsAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId)
    {
        var team = await GetTeamAsync(clubId, teamId);
        await EnsureCanRecordAsync(team, callerUserId);

        var matchStats = await _db.MatchStats
            .FirstOrDefaultAsync(s => s.TeamId == teamId && s.EventId == eventId);
        if (matchStats == null) return;

        await _db.PlayerMatchStats
            .Where(ps => ps.MatchStatsId == matchStats.MatchStatsId)
            .ExecuteDeleteAsync();
        _db.MatchStats.Remove(matchStats);
        await _db.SaveChangesAsync();
    }

    public async Task<PlayerStatsAggregateDto> GetPlayerAggregateAsync(Guid clubId, Guid teamId, Guid playerUserId, Guid callerUserId)
    {
        var team = await GetTeamAsync(clubId, teamId);
        await EnsureCanViewPlayerStatsAsync(team, playerUserId, callerUserId);

        var playerStats = await _db.PlayerMatchStats
            .Include(s => s.Player)
            .Where(s => s.TeamId == teamId && s.PlayerUserId == playerUserId)
            .ToListAsync();

        return BuildPlayerAggregates(playerStats).FirstOrDefault()
            ?? new PlayerStatsAggregateDto
            {
                PlayerUserId = playerUserId,
                PlayerName = await _db.Users.Where(u => u.UserId == playerUserId).Select(u => u.Name).FirstOrDefaultAsync() ?? "Unknown",
            };
    }

    public async Task<List<PlayerMatchStatsDto>> GetPlayerMatchHistoryAsync(Guid clubId, Guid teamId, Guid playerUserId, Guid callerUserId)
    {
        var team = await GetTeamAsync(clubId, teamId);
        await EnsureCanViewPlayerStatsAsync(team, playerUserId, callerUserId);

        return await _db.PlayerMatchStats
            .Include(s => s.Player)
            .Include(s => s.MatchStats)
            .Include(s => s.Event)
            .Where(s => s.TeamId == teamId && s.PlayerUserId == playerUserId)
            .OrderByDescending(s => s.MatchStats.UpdatedAt)
            .ThenByDescending(s => s.MatchStats.CreatedAt)
            .ThenByDescending(s => s.Event.StartAt)
            .Select(s => ToPlayerDto(s))
            .ToListAsync();
    }

    private async Task<MatchStatsDto> BuildMatchStatsDtoAsync(Guid matchStatsId, Guid callerUserId)
    {
        var entity = await _db.MatchStats
            .Include(s => s.Event)
            .Include(s => s.Recorder)
            .Include(s => s.PlayerStats)
                .ThenInclude(ps => ps.Player)
            .FirstOrDefaultAsync(s => s.MatchStatsId == matchStatsId)
            ?? throw new InvalidOperationException("Stats not found.");

        var team = await _db.Teams.FirstAsync(t => t.TeamId == entity.TeamId);
        var canViewAllPlayers = await CanViewAllPlayerStatsAsync(team, callerUserId);

        var playerRows = entity.PlayerStats.AsEnumerable();
        if (!canViewAllPlayers)
            playerRows = playerRows.Where(p => p.PlayerUserId == callerUserId);

        return new MatchStatsDto
        {
            MatchStatsId = entity.MatchStatsId,
            TeamId = entity.TeamId,
            EventId = entity.EventId,
            SeasonId = entity.SeasonId,
            EventTitle = entity.Event.Title,
            EventType = entity.Event.EventType.ToString(),
            EventStartAt = entity.Event.StartAt,
            OpponentName = entity.OpponentName,
            TeamScore = entity.TeamScore,
            OpponentScore = entity.OpponentScore,
            Result = entity.Result,
            Venue = entity.Venue,
            CompetitionName = entity.CompetitionName,
            PossessionPercent = entity.PossessionPercent,
            TotalGoals = entity.TotalGoals,
            TotalAssists = entity.TotalAssists,
            ShotsOnTarget = entity.ShotsOnTarget,
            TotalShots = entity.TotalShots,
            PassesCompleted = entity.PassesCompleted,
            PassesAttempted = entity.PassesAttempted,
            PassAccuracy = entity.PassAccuracy,
            Tackles = entity.Tackles,
            Interceptions = entity.Interceptions,
            YellowCards = entity.YellowCards,
            RedCards = entity.RedCards,
            Notes = entity.Notes,
            RecorderName = entity.Recorder.Name,
            CreatedAt = entity.CreatedAt,
            HasRawPdf = !string.IsNullOrEmpty(entity.RawPdfPath),
            RawPdfFileName = entity.RawPdfFileName,
            PlayerStats = playerRows.OrderBy(p => p.Player.Name).Select(ToPlayerDto).ToList(),
        };
    }

    private async Task<CreateStatsPreviewDto?> ParseUploadedStatsAsync(Team team, Guid eventId, string text, string extension)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var activePlayers = await _db.TeamMemberships
            .Include(tm => tm.User)
            .Where(tm => tm.TeamId == team.TeamId && tm.Role == RoleNameType.Player && tm.Status == MembershipStatus.Active)
            .Select(tm => new PlayerLookup(tm.UserId, tm.User.Name, tm.User.Email))
            .ToListAsync();

        var rows = ParseDelimitedRows(text);
        if (rows.Count == 0 && extension == ".pdf")
            rows = ParsePdfLikeRows(text);

        if (rows.Count == 0 && extension == ".pdf")
            return ParseFibaPdfStats(eventId, text, team.TeamName);

        if (rows.Count == 0) return null;

        var result = new CreateStatsPreviewDto { EventId = eventId };
        foreach (var row in rows)
        {
            var type = Get(row, "row_type", "type", "row").ToLowerInvariant();
            if (type == "team")
            {
                result.OpponentName = Get(row, "opponent_name", "opponent");
                result.TeamScore = Int(row, "team_score", "score_for", "goals_for");
                result.OpponentScore = Int(row, "opponent_score", "score_against", "goals_against");
                result.Result = NormalizeResult(Get(row, "result"));
                result.Venue = Get(row, "venue");
                result.CompetitionName = Get(row, "competition_name", "competition");
                result.PossessionPercent = Decimal(row, "possession_percent", "possession");
                result.TotalGoals = Int(row, "total_goals", "goals");
                result.TotalAssists = Int(row, "total_assists", "assists");
                result.ShotsOnTarget = Int(row, "shots_on_target", "sot");
                result.TotalShots = Int(row, "total_shots", "shots");
                result.PassesCompleted = Int(row, "passes_completed", "passes");
                result.PassesAttempted = Int(row, "passes_attempted");
                result.PassAccuracy = Decimal(row, "pass_accuracy");
                result.Tackles = Int(row, "tackles");
                result.Interceptions = Int(row, "interceptions");
                result.YellowCards = Int(row, "yellow_cards");
                result.RedCards = Int(row, "red_cards");
                result.Notes = Get(row, "notes");
            }
            else if (type == "player")
            {
                var player = MatchPlayer(row, activePlayers);
                if (player == null) continue;

                result.PlayerStats.Add(new CreatePlayerStatsPreviewDto
                {
                    PlayerUserId = player.UserId,
                    PlayerName = player.Name,
                    MinutesPlayed = Int(row, "minutes_played", "minutes"),
                    Goals = Int(row, "goals"),
                    Assists = Int(row, "assists"),
                    ShotsOnTarget = Int(row, "shots_on_target", "sot"),
                    TotalShots = Int(row, "total_shots", "shots"),
                    PassesCompleted = Int(row, "passes_completed", "passes"),
                    PassesAttempted = Int(row, "passes_attempted"),
                    PassAccuracy = Decimal(row, "pass_accuracy"),
                    Tackles = Int(row, "tackles"),
                    Interceptions = Int(row, "interceptions"),
                    YellowCards = Int(row, "yellow_cards"),
                    RedCards = Int(row, "red_cards"),
                    Rating = Decimal(row, "rating"),
                    Notes = Get(row, "notes"),
                });
            }
        }

        return !string.IsNullOrWhiteSpace(result.OpponentName) || result.PlayerStats.Count > 0 ? result : null;
    }

    private static CreateStatsPreviewDto? ParseFibaPdfStats(Guid eventId, string text, string teamName)
    {
        var match = Regex.Match(text, @"(?m)^\s*(?<home>[A-Za-z][A-Za-z .'\-]+?)\s+(?<homeScore>\d{1,3})\s*[\-\u2013\u2014]\s*(?<awayScore>\d{1,3})\s+(?<away>[A-Za-z][A-Za-z .'\-]+?)\s*$");
        if (!match.Success) return null;

        var home = new FibaTeamSide(
            match.Groups["home"].Value.Trim(),
            int.Parse(match.Groups["homeScore"].Value, CultureInfo.InvariantCulture),
            0);
        var away = new FibaTeamSide(
            match.Groups["away"].Value.Trim(),
            int.Parse(match.Groups["awayScore"].Value, CultureInfo.InvariantCulture),
            1);

        var selected = SelectFibaSide(teamName, home, away);
        var opponent = selected.Index == home.Index ? away : home;
        var fieldGoals = GetMadeAttemptByLabel(text, "Field Goals", selected.Index);
        var twoPoints = GetMadeAttemptByLabel(text, "2 Points", selected.Index);
        var threePoints = GetMadeAttemptByLabel(text, "3 Points", selected.Index);
        var freeThrows = GetMadeAttemptByLabel(text, "Free Throws", selected.Index);

        var notes = new List<string> { "Parsed from FIBA PDF report." };
        if (fieldGoals != null) notes.Add($"Field goals: {fieldGoals.Value.Made}/{fieldGoals.Value.Attempted}");
        if (twoPoints != null) notes.Add($"2 points: {twoPoints.Value.Made}/{twoPoints.Value.Attempted}");
        if (threePoints != null) notes.Add($"3 points: {threePoints.Value.Made}/{threePoints.Value.Attempted}");
        if (freeThrows != null) notes.Add($"Free throws: {freeThrows.Value.Made}/{freeThrows.Value.Attempted}");
        if (!FibaNameMatches(teamName, selected.Name))
            notes.Add($"Team side inferred as {selected.Name}; verify the preview before saving.");

        return new CreateStatsPreviewDto
        {
            EventId = eventId,
            OpponentName = opponent.Name,
            TeamScore = selected.Score,
            OpponentScore = opponent.Score,
            Result = selected.Score > opponent.Score ? "Win" : selected.Score == opponent.Score ? "Draw" : "Loss",
            Venue = ExtractFibaVenue(text),
            CompetitionName = ExtractFibaCompetition(text),
            TotalGoals = selected.Score,
            ShotsOnTarget = fieldGoals?.Made,
            TotalShots = fieldGoals?.Attempted,
            Notes = string.Join(" ", notes),
        };
    }

    private static List<Dictionary<string, string>> ParseDelimitedRows(string text)
    {
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (lines.Count < 2) return new List<Dictionary<string, string>>();

        var delimiter = lines[0].Contains(';') ? ';' : ',';
        var headers = SplitCsvLine(lines[0], delimiter).Select(NormalizeKey).ToList();
        if (!headers.Any(h => h is "row_type" or "type" or "row")) return new List<Dictionary<string, string>>();

        var rows = new List<Dictionary<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            var values = SplitCsvLine(line, delimiter);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count && i < values.Count; i++)
                row[headers[i]] = values[i].Trim();
            rows.Add(row);
        }
        return rows;
    }

    private static List<string> SplitCsvLine(string line, char delimiter)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        values.Add(current.ToString());
        return values;
    }

    private static List<Dictionary<string, string>> ParsePdfLikeRows(string text)
    {
        return text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains('|'))
            .Select(line =>
            {
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in line.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pieces = part.Split(':', 2);
                    if (pieces.Length == 2) row[NormalizeKey(pieces[0])] = pieces[1].Trim();
                }
                return row;
            })
            .Where(row => row.Count > 0)
            .ToList();
    }

    private static async Task<string> ExtractPdfTextAsync(string fileName, Stream fileContent)
    {
        var toolPath = FindPdfToText();
        if (toolPath == null)
            throw new InvalidOperationException("PDF parsing requires pdftotext to be installed. Upload CSV for built-in parsing.");

        var tempPdf = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{Path.GetFileName(fileName)}");
        var tempTxt = Path.ChangeExtension(tempPdf, ".txt");
        try
        {
            await using (var output = File.Create(tempPdf))
            {
                await fileContent.CopyToAsync(output);
            }

            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = toolPath,
                ArgumentList = { "-layout", "-enc", "UTF-8", tempPdf, tempTxt },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process == null) throw new InvalidOperationException("Could not start PDF parser.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || !File.Exists(tempTxt))
                throw new InvalidOperationException("Could not parse the uploaded PDF.");
            return await File.ReadAllTextAsync(tempTxt, Encoding.UTF8);
        }
        finally
        {
            TryDelete(tempPdf);
            TryDelete(tempTxt);
        }
    }

    private static string? FindPdfToText()
    {
        var candidates = new List<string>
        {
            @"C:\Program Files\Git\mingw64\bin\pdftotext.exe",
            @"C:\Program Files\Git\usr\bin\pdftotext.exe",
            @"C:\Program Files\poppler\Library\bin\pdftotext.exe",
            @"C:\Program Files (x86)\poppler\Library\bin\pdftotext.exe",
        };

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(pathValue
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => Path.Combine(dir, "pdftotext.exe")));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static PlayerMatchStatsDto ToPlayerDto(PlayerMatchStats s)
    {
        return new PlayerMatchStatsDto
        {
            PlayerMatchStatsId = s.PlayerMatchStatsId,
            MatchStatsId = s.MatchStatsId,
            TeamId = s.TeamId,
            EventId = s.EventId,
            SeasonId = s.SeasonId,
            PlayerUserId = s.PlayerUserId,
            PlayerName = s.Player.Name,
            EventTitle = s.Event.Title,
            EventType = s.Event.EventType.ToString(),
            EventStartAt = s.Event.StartAt,
            OpponentName = s.MatchStats.OpponentName,
            TeamScore = s.MatchStats.TeamScore,
            OpponentScore = s.MatchStats.OpponentScore,
            Category = s.MatchStats.Category,
            GameNo = s.MatchStats.GameNo,
            Matchup = s.MatchStats.Matchup,
            CreatedAt = s.MatchStats.CreatedAt,
            UpdatedAt = s.MatchStats.UpdatedAt,
            Status = s.Status,
            PlayerNo = s.PlayerNo,
            IsStarter = s.IsStarter,
            IsCaptain = s.IsCaptain,
            GamesListed = s.GamesListed,
            GamesPlayed = s.GamesPlayed,
            Starts = s.Starts,
            Minutes = s.BbMinutes,
            TwoPtMA = s.TwoPtMA,
            ThreePtMA = s.ThreePtMA,
            FtMA = s.FtMA,
            OffensiveRebounds = s.OffensiveRebounds,
            DefensiveRebounds = s.DefensiveRebounds,
            TotalRebounds = s.TotalRebounds,
            BasketballAssists = s.BbAssists,
            Turnovers = s.BbTurnovers,
            Steals = s.BbSteals,
            Blocks = s.BbBlocks,
            PersonalFouls = s.BbPersonalFouls,
            FoulsDrawn = s.BbFoulsDrawn,
            Efficiency = s.BbEfficiency,
            Points = s.BbPoints,
            MinutesPlayed = s.MinutesPlayed,
            Goals = s.Goals,
            Assists = s.Assists,
            ShotsOnTarget = s.ShotsOnTarget,
            TotalShots = s.TotalShots,
            PassesCompleted = s.PassesCompleted,
            PassesAttempted = s.PassesAttempted,
            PassAccuracy = s.PassAccuracy,
            Tackles = s.Tackles,
            Interceptions = s.Interceptions,
            YellowCards = s.YellowCards,
            RedCards = s.RedCards,
            Rating = s.Rating,
            Notes = s.Notes,
        };
    }

    private static List<PlayerStatsAggregateDto> BuildPlayerAggregates(IEnumerable<PlayerMatchStats> playerStats)
    {
        return playerStats
            .GroupBy(s => new { s.PlayerUserId, s.Player.Name })
            .Select(g => new PlayerStatsAggregateDto
            {
                PlayerUserId = g.Key.PlayerUserId,
                PlayerName = g.Key.Name,
                EventsPlayed = g.Count(),
                MinutesPlayed = g.Sum(s => s.MinutesPlayed ?? 0),
                Goals = g.Sum(s => s.Goals ?? 0),
                Assists = g.Sum(s => s.Assists ?? 0),
                ShotsOnTarget = g.Sum(s => s.ShotsOnTarget ?? 0),
                TotalShots = g.Sum(s => s.TotalShots ?? 0),
                PassesCompleted = g.Sum(s => s.PassesCompleted ?? 0),
                PassesAttempted = g.Sum(s => s.PassesAttempted ?? 0),
                AveragePassAccuracy = AverageNullable(g.Select(s => s.PassAccuracy)),
                Tackles = g.Sum(s => s.Tackles ?? 0),
                Interceptions = g.Sum(s => s.Interceptions ?? 0),
                YellowCards = g.Sum(s => s.YellowCards ?? 0),
                RedCards = g.Sum(s => s.RedCards ?? 0),
                AverageRating = AverageNullable(g.Select(s => s.Rating)),
            })
            .OrderByDescending(p => p.Goals)
            .ThenByDescending(p => p.Assists)
            .ThenByDescending(p => p.AverageRating ?? 0)
            .ToList();
    }

    private static void ApplyTeamStats(MatchStats entity, CreateMatchStatsRequest request, DateTime now)
    {
        entity.OpponentName = Clean(request.OpponentName);
        entity.TeamScore = request.TeamScore;
        entity.OpponentScore = request.OpponentScore;
        entity.Result = NormalizeResult(request.Result);
        entity.Venue = Clean(request.Venue);
        entity.CompetitionName = Clean(request.CompetitionName);
        entity.PossessionPercent = request.PossessionPercent;
        entity.TotalGoals = request.TotalGoals ?? request.TeamScore;
        entity.TotalAssists = request.TotalAssists;
        entity.ShotsOnTarget = request.ShotsOnTarget;
        entity.TotalShots = request.TotalShots;
        entity.PassesCompleted = request.PassesCompleted;
        entity.PassesAttempted = request.PassesAttempted;
        entity.PassAccuracy = NormalizeAccuracy(request.PassAccuracy, request.PassesCompleted, request.PassesAttempted);
        entity.Tackles = request.Tackles;
        entity.Interceptions = request.Interceptions;
        entity.YellowCards = request.YellowCards;
        entity.RedCards = request.RedCards;
        entity.Notes = Clean(request.Notes);
        entity.UpdatedAt = now;
    }

    private async Task EnsureCanRecordAsync(Team team, Guid userId)
    {
        if (await IsAdminAsync(userId)) return;
        if (team.ClubId.HasValue && await _db.Clubs.AnyAsync(c => c.ClubId == team.ClubId && c.CreatedBy == userId)) return;

        var role = await GetTeamRoleAsync(team.TeamId, userId);
        if (role is RoleNameType.TeamAnalyst or RoleNameType.TeamManager or RoleNameType.Coach) return;

        var isClubManager = team.ClubId.HasValue && await _db.ClubMemberships.AnyAsync(cm =>
            cm.ClubId == team.ClubId &&
            cm.UserId == userId &&
            cm.Status == MembershipStatus.Active &&
            cm.Role == RoleNameType.ClubManager);
        if (isClubManager) return;

        throw new UnauthorizedAccessException("Only analysts, coaches, and managers can record stats.");
    }

    private async Task EnsureCanViewTeamAsync(Team team, Guid userId)
    {
        if (await IsAdminAsync(userId)) return;
        if (team.ClubId.HasValue && await _db.Clubs.AnyAsync(c => c.ClubId == team.ClubId && c.CreatedBy == userId)) return;

        var hasTeamMembership = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == team.TeamId &&
            tm.UserId == userId &&
            tm.Status == MembershipStatus.Active);

        var hasClubMembership = team.ClubId.HasValue && await _db.ClubMemberships.AnyAsync(cm =>
            cm.ClubId == team.ClubId &&
            cm.UserId == userId &&
            cm.Status == MembershipStatus.Active);

        if (!hasTeamMembership && !hasClubMembership)
            throw new UnauthorizedAccessException("You do not have access to this team.");
    }

    private async Task EnsureCanViewPlayerStatsAsync(Team team, Guid playerUserId, Guid callerUserId)
    {
        await EnsureCanViewTeamAsync(team, callerUserId);
        if (playerUserId == callerUserId) return;
        if (await CanViewAllPlayerStatsAsync(team, callerUserId)) return;
        throw new UnauthorizedAccessException("Players can only view their own stats.");
    }

    private async Task<bool> CanViewAllPlayerStatsAsync(Team team, Guid userId)
    {
        if (await IsAdminAsync(userId)) return true;
        if (team.ClubId.HasValue && await _db.Clubs.AnyAsync(c => c.ClubId == team.ClubId && c.CreatedBy == userId)) return true;

        var role = await GetTeamRoleAsync(team.TeamId, userId);
        if (role is RoleNameType.TeamManager or RoleNameType.Coach or RoleNameType.TeamAnalyst or RoleNameType.FitnessCoach or RoleNameType.TeamDoctor)
            return true;

        return team.ClubId.HasValue && await _db.ClubMemberships.AnyAsync(cm =>
            cm.ClubId == team.ClubId &&
            cm.UserId == userId &&
            cm.Status == MembershipStatus.Active &&
            (cm.Role == RoleNameType.ClubManager || cm.Role == RoleNameType.TeamManager));
    }

    private Task<RoleNameType?> GetTeamRoleAsync(Guid teamId, Guid userId)
    {
        return _db.TeamMemberships
            .Where(tm => tm.TeamId == teamId && tm.UserId == userId && tm.Status == MembershipStatus.Active)
            .Select(tm => (RoleNameType?)tm.Role)
            .FirstOrDefaultAsync();
    }

    private async Task<Team> GetTeamAsync(Guid clubId, Guid teamId)
    {
        return await _db.Teams.FirstOrDefaultAsync(t => t.TeamId == teamId && t.ClubId == clubId && t.DeletedAt == null)
            ?? throw new InvalidOperationException("Team not found.");
    }

    private Task<bool> IsAdminAsync(Guid userId) => _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin);

    private static decimal? NormalizeAccuracy(decimal? value, int? completed, int? attempted)
    {
        if (value.HasValue) return value.Value;
        if (completed.HasValue && attempted.HasValue && attempted.Value > 0)
            return Math.Round((decimal)completed.Value / attempted.Value * 100, 2);
        return null;
    }

    private static decimal? AverageNullable(IEnumerable<decimal?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : Math.Round(present.Average(), 2);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeResult(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = value.Trim();
        return clean.ToLowerInvariant() switch
        {
            "w" or "win" => "Win",
            "d" or "draw" => "Draw",
            "l" or "loss" or "lose" => "Loss",
            _ => clean,
        };
    }

    private static string NormalizeKey(string value)
    {
        return value.Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
    }

    private static string Get(Dictionary<string, string> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (row.TryGetValue(NormalizeKey(key), out var value))
                return value.Trim();
        }
        return string.Empty;
    }

    private static int? Int(Dictionary<string, string> row, params string[] keys)
    {
        var value = Get(row, keys);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static decimal? Decimal(Dictionary<string, string> row, params string[] keys)
    {
        var value = Get(row, keys).TrimEnd('%');
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static PlayerLookup? MatchPlayer(Dictionary<string, string> row, IEnumerable<PlayerLookup> players)
    {
        var userIdText = Get(row, "player_user_id", "user_id", "player_id");
        if (Guid.TryParse(userIdText, out var userId))
            return players.FirstOrDefault(p => p.UserId == userId);

        var email = Get(row, "player_email", "email");
        if (!string.IsNullOrWhiteSpace(email))
            return players.FirstOrDefault(p => string.Equals(p.Email, email, StringComparison.OrdinalIgnoreCase));

        var name = Get(row, "player_name", "name", "player");
        if (!string.IsNullOrWhiteSpace(name))
            return players.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        return null;
    }

    private static FibaTeamSide SelectFibaSide(string teamName, FibaTeamSide home, FibaTeamSide away)
    {
        var homeMatches = FibaNameMatches(teamName, home.Name);
        var awayMatches = FibaNameMatches(teamName, away.Name);
        if (homeMatches && !awayMatches) return home;
        if (awayMatches && !homeMatches) return away;

        return away;
    }

    private static bool FibaNameMatches(string teamName, string reportName)
    {
        var team = NormalizeFibaName(teamName);
        var report = NormalizeFibaName(reportName);
        if (string.IsNullOrWhiteSpace(team) || string.IsNullOrWhiteSpace(report)) return false;
        return team.Contains(report, StringComparison.OrdinalIgnoreCase) ||
               report.Contains(team, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFibaName(string value)
    {
        return Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", string.Empty);
    }

    private static MadeAttempt? GetMadeAttemptByLabel(string text, string label, int sideIndex)
    {
        var matches = Regex.Matches(text, $@"\b{Regex.Escape(label)}\s+(?<made>\d+)\s*/\s*(?<attempted>\d+)");
        if (matches.Count <= sideIndex) return null;

        var match = matches[sideIndex];
        return new MadeAttempt(
            int.Parse(match.Groups["made"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["attempted"].Value, CultureInfo.InvariantCulture));
    }

    private static string? ExtractFibaVenue(string text)
    {
        var match = Regex.Match(text, @"(?m)^\s*(?<venue>.+?),\s*(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)\s+\d{2}\s+\w+\s+\d{4}\s+Start time:");
        return match.Success ? Clean(match.Groups["venue"].Value) : null;
    }

    private static string? ExtractFibaCompetition(string text)
    {
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Regex.Replace(line, @"\s{2,}", " ").Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeWhile(line => !line.Contains("Game No.:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line
                .Replace("Shot Chart", "", StringComparison.OrdinalIgnoreCase)
                .Replace("FIBA Box Score", "", StringComparison.OrdinalIgnoreCase)
                .Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count == 0) return null;
        return string.Join(" ", lines.Take(2));
    }

    // ── Basketball-specific implementations ──

    public async Task<BasketballUploadPreviewDto> ExtractBasketballPdfAsync(
        Guid clubId, Guid teamId, Guid callerUserId, string fileName, Stream fileContent)
    {
        var team = await GetTeamAsync(clubId, teamId);
        await EnsureCanRecordAsync(team, callerUserId);

        // Read file into memory to send to the FastAPI extractor
        using var memoryStream = new MemoryStream();
        await fileContent.CopyToAsync(memoryStream);
        var fileBytes = memoryStream.ToArray();

        try
        {
            var httpClient = _httpClientFactory.CreateClient(StatsExtractorHttpClientName);
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(fileBytes), "file", Path.GetFileName(fileName));

            var response = await httpClient.PostAsync("/extract", form);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var sidecarResult = System.Text.Json.JsonSerializer.Deserialize<BasketballSidecarResponse>(json,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });

            if (sidecarResult?.Rows == null || sidecarResult.Rows.Count == 0)
            {
                return new BasketballUploadPreviewDto
                {
                    FileName = fileName,
                    CanSave = false,
                    Message = "The PDF did not contain recognizable basketball stats.",
                };
            }

            var mappedRows = sidecarResult.Rows.Select(MapSidecarRow).ToList();
            return new BasketballUploadPreviewDto
            {
                FileName = fileName,
                CanSave = true,
                Message = $"Extracted {mappedRows.Count} rows successfully.",
                RowCount = mappedRows.Count,
                PlayerCount = mappedRows.Count(r => r.RowType == "player"),
                TeamTotalCount = mappedRows.Count(r => r.RowType == "team_total"),
                Rows = mappedRows,
            };
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(
                "Stats extraction service is unavailable. Check StatsExtractor:BaseUrl and the extractor container.");
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("Stats extraction timed out. The PDF may be too large.");
        }
    }

    public async Task<BasketballMatchStatsDto> CreateBasketballStatsAsync(
        Guid clubId, Guid teamId, Guid callerUserId, CreateBasketballStatsRequest request)
    {
        var team = await GetTeamAsync(clubId, teamId);
        await EnsureCanRecordAsync(team, callerUserId);

        var gameEvent = await _db.Events
            .FirstOrDefaultAsync(e => e.EventId == request.EventId && e.TeamId == teamId)
            ?? throw new InvalidOperationException("Event not found for this team.");

        var now = DateTime.UtcNow;
        var result = NormalizeResult(
            request.TeamScore > request.OpponentScore ? "Win" :
            request.TeamScore < request.OpponentScore ? "Loss" : "Draw");

        var entity = new MatchStats
        {
            MatchStatsId = Guid.NewGuid(),
            TeamId = teamId,
            EventId = gameEvent.EventId,
            SeasonId = gameEvent.SeasonId,
            RecordedBy = callerUserId,
            Category = request.Category,
            OpponentName = Clean(request.OpponentName),
            TeamScore = request.TeamScore,
            OpponentScore = request.OpponentScore,
            Result = result,
            Venue = Clean(request.Venue),
            CompetitionName = Clean(request.CompetitionName),
            GameNo = request.GameNo,
            Matchup = request.Matchup,
            Granularity = "game_team_total",
            TwoPtMA = request.TwoPtMA,
            ThreePtMA = request.ThreePtMA,
            FtMA = request.FtMA,
            OffensiveRebounds = request.OffensiveRebounds,
            DefensiveRebounds = request.DefensiveRebounds,
            TotalRebounds = request.TotalRebounds,
            BbAssists = request.Assists,
            Turnovers = request.Turnovers,
            Steals = request.Steals,
            Blocks = request.Blocks,
            PersonalFouls = request.PersonalFouls,
            FoulsDrawn = request.FoulsDrawn,
            Efficiency = request.Efficiency,
            Points = request.Points,
            Minutes = request.Minutes,
            Notes = Clean(request.Notes),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.MatchStats.Add(entity);

        foreach (var p in request.PlayerStats)
        {
            entity.PlayerStats.Add(new PlayerMatchStats
            {
                PlayerMatchStatsId = Guid.NewGuid(),
                MatchStatsId = entity.MatchStatsId,
                TeamId = teamId,
                EventId = gameEvent.EventId,
                SeasonId = gameEvent.SeasonId,
                PlayerUserId = p.PlayerUserId ?? Guid.Empty,
                Granularity = "game_player",
                RowType = "player",
                Status = p.Status,
                PlayerNo = p.PlayerNo,
                IsStarter = p.IsStarter,
                IsCaptain = p.IsCaptain,
                BbMinutes = p.Minutes,
                TwoPtMA = p.TwoPtMA,
                ThreePtMA = p.ThreePtMA,
                FtMA = p.FtMA,
                OffensiveRebounds = p.OffensiveRebounds,
                DefensiveRebounds = p.DefensiveRebounds,
                TotalRebounds = p.TotalRebounds,
                BbAssists = p.Assists,
                BbTurnovers = p.Turnovers,
                BbSteals = p.Steals,
                BbBlocks = p.Blocks,
                BbPersonalFouls = p.PersonalFouls,
                BbFoulsDrawn = p.FoulsDrawn,
                BbEfficiency = p.Efficiency,
                BbPoints = p.Points,
                Notes = Clean(p.Notes),
            });
        }

        await _db.SaveChangesAsync();
        await NotifyStatsSavedAsync(team, callerUserId, gameEvent.EventId, gameEvent.Title, entity.MatchStatsId);
        return BuildBasketballMatchDto(entity);
    }

    public async Task<BasketballMatchStatsDto> ConfirmBasketballUploadAsync(
        Guid clubId, Guid teamId, Guid callerUserId, ConfirmBasketballUploadRequest request)
    {
        var team = await GetTeamAsync(clubId, teamId);
        await EnsureCanRecordAsync(team, callerUserId);

        var gameEvent = await _db.Events
            .FirstOrDefaultAsync(e => e.EventId == request.EventId && e.TeamId == teamId)
            ?? throw new InvalidOperationException("Event not found for this team.");

        var now = DateTime.UtcNow;
        var jerseyMap = await GetActivePlayerJerseyMapAsync(teamId);
        var selection = SelectUploadedTeamRows(team, request.Rows, jerseyMap);
        var playerRows = selection.PlayerRows
            .Where(IsPlayedBasketballRow)
            .ToList();

        if (playerRows.Count == 0)
            throw new InvalidOperationException("The uploaded table does not contain any played player rows for this team.");

        var duplicateUploadedNumbers = playerRows
            .Where(r => r.PlayerNo.HasValue)
            .GroupBy(r => r.PlayerNo!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateUploadedNumbers.Count > 0)
            throw new InvalidOperationException($"The uploaded table contains duplicate player numbers: {string.Join(", ", duplicateUploadedNumbers)}.");

        var mappedPlayerRows = playerRows
            .Where(r => r.PlayerNo.HasValue && jerseyMap.ContainsKey(r.PlayerNo.Value))
            .ToList();
        if (mappedPlayerRows.Count == 0)
            throw new InvalidOperationException(
                "Could not map any played uploaded player rows to active team players by jersey number.");

        var skippedPlayerCount = playerRows.Count - mappedPlayerRows.Count;

        var teamTotalRow = selection.TeamTotalRow ?? BuildTeamTotalRow(playerRows);
        var opponentTotalRow = selection.OpponentTeamTotalRow;
        var existing = await _db.MatchStats
            .FirstOrDefaultAsync(s => s.TeamId == teamId && s.EventId == request.EventId);

        MatchStats entity;
        if (existing == null)
        {
            entity = new MatchStats
            {
                MatchStatsId = Guid.NewGuid(),
                TeamId = teamId,
                EventId = gameEvent.EventId,
                SeasonId = gameEvent.SeasonId,
                CreatedAt = now,
            };
            _db.MatchStats.Add(entity);
        }
        else
        {
            entity = existing;
            await _db.PlayerMatchStats
                .Where(ps => ps.MatchStatsId == entity.MatchStatsId)
                .ExecuteDeleteAsync();
        }

        var teamScore = teamTotalRow.TeamScore ?? teamTotalRow.Points;
        var opponentScore = opponentTotalRow?.Points ?? teamTotalRow.OpponentScore;
        var opponentName = Clean(opponentTotalRow?.TeamName)
            ?? Clean(teamTotalRow.OpponentName)
            ?? ExtractOpponentName(teamTotalRow.Matchup, teamTotalRow.TeamName);
        var result = GetResultFromScores(teamScore, opponentScore);
        entity.RecordedBy = callerUserId;
        entity.Category = request.Category;
        entity.Granularity = "game_team_total";
        entity.OpponentName = opponentName;
        entity.TeamScore = teamScore;
        entity.OpponentScore = opponentScore;
        entity.Result = result;
        entity.GameNo = teamTotalRow.GameNo;
        entity.Matchup = teamTotalRow.Matchup;
        entity.TwoPtMA = teamTotalRow.TwoPtMA;
        entity.ThreePtMA = teamTotalRow.ThreePtMA;
        entity.FtMA = teamTotalRow.FtMA;
        entity.OffensiveRebounds = teamTotalRow.OffensiveRebounds;
        entity.DefensiveRebounds = teamTotalRow.DefensiveRebounds;
        entity.TotalRebounds = teamTotalRow.TotalRebounds;
        entity.BbAssists = teamTotalRow.Assists;
        entity.Turnovers = teamTotalRow.Turnovers;
        entity.Steals = teamTotalRow.Steals;
        entity.Blocks = teamTotalRow.Blocks;
        entity.PersonalFouls = teamTotalRow.PersonalFouls;
        entity.FoulsDrawn = teamTotalRow.FoulsDrawn;
        entity.Efficiency = teamTotalRow.Efficiency;
        entity.Points = teamTotalRow.Points;
        entity.Minutes = teamTotalRow.Minutes;
        entity.TeamOffReb = teamTotalRow.TeamOffReb;
        entity.TeamDefReb = teamTotalRow.TeamDefReb;
        entity.TeamReb = teamTotalRow.TeamReb;
        entity.TeamPF = teamTotalRow.TeamPF;
        entity.TeamFD = teamTotalRow.TeamFD;
        entity.SourceFile = teamTotalRow.SourceFile;
        entity.Notes = skippedPlayerCount > 0
            ? $"Imported from FIBA PDF box score. Saved {mappedPlayerRows.Count} active player row(s); skipped {skippedPlayerCount} played row(s) that are not on the active roster."
            : "Imported from FIBA PDF box score.";
        entity.UpdatedAt = now;

        var playerStatsToSave = new List<PlayerMatchStats>();
        foreach (var p in mappedPlayerRows)
        {
            var matchedPlayer = jerseyMap[p.PlayerNo!.Value];
            playerStatsToSave.Add(new PlayerMatchStats
            {
                PlayerMatchStatsId = Guid.NewGuid(),
                MatchStatsId = entity.MatchStatsId,
                TeamId = teamId,
                EventId = gameEvent.EventId,
                SeasonId = gameEvent.SeasonId,
                PlayerUserId = matchedPlayer.UserId,
                Granularity = p.Granularity,
                RowType = p.RowType,
                Status = p.Status,
                PlayerNo = p.PlayerNo,
                IsStarter = p.IsStarter,
                IsCaptain = p.IsCaptain,
                GamesListed = p.GamesListed,
                GamesPlayed = p.GamesPlayed,
                Starts = p.Starts,
                BbMinutes = p.Minutes,
                TwoPtMA = p.TwoPtMA,
                ThreePtMA = p.ThreePtMA,
                FtMA = p.FtMA,
                OffensiveRebounds = p.OffensiveRebounds,
                DefensiveRebounds = p.DefensiveRebounds,
                TotalRebounds = p.TotalRebounds,
                BbAssists = p.Assists,
                BbTurnovers = p.Turnovers,
                BbSteals = p.Steals,
                BbBlocks = p.Blocks,
                BbPersonalFouls = p.PersonalFouls,
                BbFoulsDrawn = p.FoulsDrawn,
                BbEfficiency = p.Efficiency,
                BbPoints = p.Points,
                BbTeamOffReb = p.TeamOffReb,
                BbTeamDefReb = p.TeamDefReb,
                BbTeamReb = p.TeamReb,
                BbTeamPF = p.TeamPF,
                BbTeamFD = p.TeamFD,
            });
        }
        _db.PlayerMatchStats.AddRange(playerStatsToSave);

        await _db.SaveChangesAsync();
        await NotifyStatsSavedAsync(team, callerUserId, gameEvent.EventId, gameEvent.Title, entity.MatchStatsId);
        var saved = await _db.MatchStats
            .Include(s => s.PlayerStats)
                .ThenInclude(ps => ps.Player)
            .FirstAsync(s => s.MatchStatsId == entity.MatchStatsId);
        return BuildBasketballMatchDto(saved);
    }

    public async Task<BasketballTeamAggregateDto> GetBasketballAggregatesAsync(
        Guid clubId, Guid teamId, Guid callerUserId)
    {
        var team = await GetTeamAsync(clubId, teamId);
        await EnsureCanViewTeamAsync(team, callerUserId);

        var stats = await _db.MatchStats
            .Include(s => s.PlayerStats)
            .Where(s => s.TeamId == teamId && s.Category != null)
            .ToListAsync();

        var gameStats = stats.Where(s => s.Category == "game").ToList();
        var allPlayerStats = stats.SelectMany(s => s.PlayerStats).Where(p => p.Granularity == "game_player").ToList();

        return new BasketballTeamAggregateDto
        {
            TotalGames = gameStats.Count,
            Wins = gameStats.Count(s => s.Result == "Win"),
            Losses = gameStats.Count(s => s.Result == "Loss"),
            TotalPoints = gameStats.Sum(s => s.Points ?? 0),
            TotalRebounds = gameStats.Sum(s => s.TotalRebounds ?? 0),
            TotalAssists = gameStats.Sum(s => s.BbAssists ?? 0),
            TotalSteals = gameStats.Sum(s => s.Steals ?? 0),
            TotalBlocks = gameStats.Sum(s => s.Blocks ?? 0),
            TotalTurnovers = gameStats.Sum(s => s.Turnovers ?? 0),
            TotalTwoPtMA = SumMadeAttempt(gameStats.Select(s => s.TwoPtMA)),
            TotalThreePtMA = SumMadeAttempt(gameStats.Select(s => s.ThreePtMA)),
            TotalFtMA = SumMadeAttempt(gameStats.Select(s => s.FtMA)),
            PlayerLeaderboard = BuildBasketballPlayerAggregates(allPlayerStats),
        };
    }

    private async Task<Dictionary<int, JerseyPlayerLookup>> GetActivePlayerJerseyMapAsync(Guid teamId)
    {
        var roster = await (
            from membership in _db.TeamMemberships
            join profile in _db.PlayerProfiles on membership.UserId equals profile.UserId
            join user in _db.Users on membership.UserId equals user.UserId
            where membership.TeamId == teamId
                && membership.Role == RoleNameType.Player
                && membership.Status == MembershipStatus.Active
                && profile.JerseyNumber.HasValue
            select new JerseyPlayerLookup(
                user.UserId,
                user.Name,
                profile.JerseyNumber!.Value))
            .ToListAsync();

        if (roster.Count == 0)
            throw new InvalidOperationException("No active players with jersey numbers were found for this team.");

        var duplicateJerseys = roster
            .GroupBy(p => p.JerseyNumber)
            .Where(g => g.Count() > 1)
            .Select(g => $"#{g.Key}")
            .ToList();
        if (duplicateJerseys.Count > 0)
            throw new InvalidOperationException(
                $"Multiple active players have the same jersey number: {string.Join(", ", duplicateJerseys)}.");

        return roster.ToDictionary(p => p.JerseyNumber);
    }

    private static BasketballUploadSelection SelectUploadedTeamRows(
        Team team,
        List<BasketballExtractedRow> rows,
        IReadOnlyDictionary<int, JerseyPlayerLookup> jerseyMap)
    {
        var gameRows = rows
            .Where(r => r.Granularity is null || r.Granularity.StartsWith("game_", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var groups = gameRows
            .GroupBy(UploadedTeamKey)
            .Select(g =>
            {
                var groupRows = g.ToList();
                var playerRows = groupRows
                    .Where(r => string.Equals(r.RowType, "player", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(r.Granularity, "game_player", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var teamTotal = groupRows.FirstOrDefault(r =>
                    string.Equals(r.RowType, "team_total", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.Granularity, "game_team_total", StringComparison.OrdinalIgnoreCase));
                var score = ScoreUploadedTeamGroup(team, playerRows, teamTotal, jerseyMap);
                return new UploadedTeamGroup(g.Key, playerRows, teamTotal, score);
            })
            .Where(g => g.PlayerRows.Count > 0 || g.TeamTotalRow != null)
            .OrderByDescending(g => g.Score)
            .ToList();

        if (groups.Count == 0)
            throw new InvalidOperationException("The uploaded table did not include any game player or team total rows.");

        var selected = groups.First();
        var opponentTotal = groups
            .Skip(1)
            .Select(g => g.TeamTotalRow)
            .FirstOrDefault(r => r != null);

        return new BasketballUploadSelection(selected.PlayerRows, selected.TeamTotalRow, opponentTotal);
    }

    private static int ScoreUploadedTeamGroup(
        Team team,
        List<BasketballExtractedRow> playerRows,
        BasketballExtractedRow? teamTotalRow,
        IReadOnlyDictionary<int, JerseyPlayerLookup> jerseyMap)
    {
        var score = 0;
        var extractedTeamName = teamTotalRow?.TeamName ?? playerRows.FirstOrDefault()?.TeamName;
        if (NamesLookRelated(team.TeamName, extractedTeamName))
            score += 30;

        foreach (var row in playerRows.Where(IsPlayedBasketballRow))
        {
            if (!row.PlayerNo.HasValue) continue;
            if (!jerseyMap.TryGetValue(row.PlayerNo.Value, out var player)) continue;

            score += 10;
            if (NamesLookRelated(player.Name, row.PlayerName))
                score += 5;
        }

        return score;
    }

    private static string UploadedTeamKey(BasketballExtractedRow row)
    {
        var code = Clean(row.TeamCode) ?? "";
        var name = Clean(row.TeamName) ?? "";
        return $"{code}|{name}".ToUpperInvariant();
    }

    private static bool IsPlayedBasketballRow(BasketballExtractedRow row)
    {
        if (string.Equals(row.Status, "DNP", StringComparison.OrdinalIgnoreCase))
            return false;

        return ParseBasketballMinutes(row.Minutes) > 0
            || row.Points.GetValueOrDefault() > 0
            || row.TotalRebounds.GetValueOrDefault() > 0
            || row.Assists.GetValueOrDefault() > 0
            || row.Steals.GetValueOrDefault() > 0
            || row.Blocks.GetValueOrDefault() > 0;
    }

    private static BasketballExtractedRow BuildTeamTotalRow(List<BasketballExtractedRow> playerRows)
    {
        var first = playerRows.First();
        return new BasketballExtractedRow
        {
            Granularity = "game_team_total",
            RowType = "team_total",
            SourceFile = first.SourceFile,
            GameNo = first.GameNo,
            GameDate = first.GameDate,
            StartTime = first.StartTime,
            Matchup = first.Matchup,
            TeamCode = first.TeamCode,
            TeamName = first.TeamName,
            TeamScore = first.TeamScore,
            OpponentName = first.OpponentName,
            OpponentScore = first.OpponentScore,
            Status = "PLAYED",
            Minutes = FormatBasketballMinutes(playerRows.Sum(p => ParseBasketballMinutes(p.Minutes))),
            TwoPtMA = SumMadeAttempt(playerRows.Select(p => p.TwoPtMA)),
            ThreePtMA = SumMadeAttempt(playerRows.Select(p => p.ThreePtMA)),
            FtMA = SumMadeAttempt(playerRows.Select(p => p.FtMA)),
            OffensiveRebounds = playerRows.Sum(p => p.OffensiveRebounds ?? 0),
            DefensiveRebounds = playerRows.Sum(p => p.DefensiveRebounds ?? 0),
            TotalRebounds = playerRows.Sum(p => p.TotalRebounds ?? 0),
            Assists = playerRows.Sum(p => p.Assists ?? 0),
            Turnovers = playerRows.Sum(p => p.Turnovers ?? 0),
            Steals = playerRows.Sum(p => p.Steals ?? 0),
            Blocks = playerRows.Sum(p => p.Blocks ?? 0),
            PersonalFouls = playerRows.Sum(p => p.PersonalFouls ?? 0),
            FoulsDrawn = playerRows.Sum(p => p.FoulsDrawn ?? 0),
            Efficiency = playerRows.Sum(p => p.Efficiency ?? 0),
            Points = playerRows.Sum(p => p.Points ?? 0),
        };
    }

    private static string? GetResultFromScores(int? teamScore, int? opponentScore)
    {
        if (!teamScore.HasValue || !opponentScore.HasValue) return null;
        return NormalizeResult(teamScore > opponentScore ? "Win" :
            teamScore < opponentScore ? "Loss" : "Draw");
    }

    private static int ParseBasketballMinutes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var match = Regex.Match(value.Trim(), @"^(?<minutes>\d+):(?<seconds>\d{2})$");
        if (!match.Success) return 0;
        return int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture) * 60
            + int.Parse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture);
    }

    private static string FormatBasketballMinutes(int totalSeconds)
    {
        return $"{totalSeconds / 60}:{totalSeconds % 60:00}";
    }

    private static bool NamesLookRelated(string? left, string? right)
    {
        var normalizedLeft = NormalizeForMatch(left);
        var normalizedRight = NormalizeForMatch(right);
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0) return false;
        return normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal)
            || normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal);
    }

    private static string? ExtractOpponentName(string? matchup, string? teamName)
    {
        if (string.IsNullOrWhiteSpace(matchup)) return null;
        var parts = Regex.Split(matchup.Trim(), @"\s+vs\s+", RegexOptions.IgnoreCase)
            .Select(Clean)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        if (parts.Count != 2) return null;

        var teamPart = parts.FirstOrDefault(part => NamesLookRelated(part, teamName));
        if (teamPart != null)
            return parts.FirstOrDefault(part => !string.Equals(part, teamPart, StringComparison.OrdinalIgnoreCase));

        return parts[1];
    }

    private static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return Regex.Replace(value.ToUpperInvariant(), @"[^A-Z0-9]", "");
    }

    private static BasketballMatchStatsDto BuildBasketballMatchDto(MatchStats entity)
    {
        return new BasketballMatchStatsDto
        {
            MatchStatsId = entity.MatchStatsId,
            TeamId = entity.TeamId,
            EventId = entity.EventId,
            Category = entity.Category ?? "game",
            OpponentName = entity.OpponentName,
            TeamScore = entity.TeamScore,
            OpponentScore = entity.OpponentScore,
            Result = entity.Result,
            Venue = entity.Venue,
            CompetitionName = entity.CompetitionName,
            GameNo = entity.GameNo,
            Matchup = entity.Matchup,
            TwoPtMA = entity.TwoPtMA,
            ThreePtMA = entity.ThreePtMA,
            FtMA = entity.FtMA,
            OffensiveRebounds = entity.OffensiveRebounds,
            DefensiveRebounds = entity.DefensiveRebounds,
            TotalRebounds = entity.TotalRebounds,
            Assists = entity.BbAssists,
            Turnovers = entity.Turnovers,
            Steals = entity.Steals,
            Blocks = entity.Blocks,
            PersonalFouls = entity.PersonalFouls,
            FoulsDrawn = entity.FoulsDrawn,
            Efficiency = entity.Efficiency,
            Points = entity.Points,
            Minutes = entity.Minutes,
            Notes = entity.Notes,
            CreatedAt = entity.CreatedAt,
            PlayerStats = entity.PlayerStats.Select(p => new BasketballPlayerStatsDto
            {
                PlayerMatchStatsId = p.PlayerMatchStatsId,
                PlayerUserId = p.PlayerUserId == Guid.Empty ? null : p.PlayerUserId,
                PlayerName = p.Player?.Name,
                PlayerNo = p.PlayerNo,
                Status = p.Status,
                IsStarter = p.IsStarter ?? false,
                IsCaptain = p.IsCaptain ?? false,
                Minutes = p.BbMinutes,
                TwoPtMA = p.TwoPtMA,
                ThreePtMA = p.ThreePtMA,
                FtMA = p.FtMA,
                OffensiveRebounds = p.OffensiveRebounds,
                DefensiveRebounds = p.DefensiveRebounds,
                TotalRebounds = p.TotalRebounds,
                Assists = p.BbAssists,
                Turnovers = p.BbTurnovers,
                Steals = p.BbSteals,
                Blocks = p.BbBlocks,
                PersonalFouls = p.BbPersonalFouls,
                FoulsDrawn = p.BbFoulsDrawn,
                Efficiency = p.BbEfficiency,
                Points = p.BbPoints,
            }).ToList(),
        };
    }

    private static List<BasketballPlayerAggregateDto> BuildBasketballPlayerAggregates(IEnumerable<PlayerMatchStats> playerStats)
    {
        return playerStats
            .Where(p => p.RowType == "player")
            .GroupBy(p => new { p.PlayerNo, Name = p.Player?.Name ?? $"#{p.PlayerNo}" })
            .Select(g => new BasketballPlayerAggregateDto
            {
                PlayerUserId = g.First().PlayerUserId == Guid.Empty ? null : g.First().PlayerUserId,
                PlayerName = g.Key.Name,
                PlayerNo = g.Key.PlayerNo,
                GamesPlayed = g.Count(p => p.Status == "PLAYED"),
                TotalPoints = g.Sum(p => p.BbPoints ?? 0),
                TotalRebounds = g.Sum(p => p.TotalRebounds ?? 0),
                TotalAssists = g.Sum(p => p.BbAssists ?? 0),
                TotalSteals = g.Sum(p => p.BbSteals ?? 0),
                TotalBlocks = g.Sum(p => p.BbBlocks ?? 0),
                TotalTurnovers = g.Sum(p => p.BbTurnovers ?? 0),
                TotalEfficiency = g.Sum(p => p.BbEfficiency ?? 0),
                TotalTwoPtMA = SumMadeAttempt(g.Select(p => p.TwoPtMA)),
                TotalThreePtMA = SumMadeAttempt(g.Select(p => p.ThreePtMA)),
                TotalFtMA = SumMadeAttempt(g.Select(p => p.FtMA)),
            })
            .OrderByDescending(p => p.TotalPoints)
            .ThenByDescending(p => p.TotalEfficiency)
            .ToList();
    }

    private Task NotifyStatsSavedAsync(Team team, Guid actorUserId, Guid eventId, string eventTitle, Guid matchStatsId)
    {
        return _notifications.CreateForTeamAsync(team.TeamId, actorUserId, new CreateNotificationRequest
        {
            ClubId = team.ClubId,
            TeamId = team.TeamId,
            Type = "StatsRecorded",
            Priority = "Normal",
            DeliveryPolicy = "RealtimeIfConnected",
            Title = "New stats recorded",
            Body = eventTitle,
            TargetType = "Stats",
            TargetId = matchStatsId,
            TargetRoute = $"/teams/{team.TeamId}/stats/{eventId}"
        });
    }

    private static string? SumMadeAttempt(IEnumerable<string?> values)
    {
        int totalMade = 0, totalAttempted = 0;
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            var match = Regex.Match(v.Trim(), @"^(\d+)\s*/\s*(\d+)$");
            if (!match.Success) continue;
            totalMade += int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            totalAttempted += int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        return totalAttempted == 0 ? null : $"{totalMade}/{totalAttempted}";
    }

    private static List<BasketballExtractedRowDto> ParseExtractedCsv(string csvText)
    {
        var lines = csvText.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return new List<BasketballExtractedRowDto>();

        var headers = SplitCsvLine(lines[0], ',').Select(h => h.Trim().ToLowerInvariant()).ToList();
        var rows = new List<BasketballExtractedRowDto>();

        foreach (var line in lines.Skip(1))
        {
            var values = SplitCsvLine(line, ',');
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count && i < values.Count; i++)
                dict[headers[i]] = values[i].Trim();

            rows.Add(new BasketballExtractedRowDto
            {
                Granularity = GetVal(dict, "granularity"),
                RowType = GetVal(dict, "row_type"),
                SourceFile = GetVal(dict, "source_file"),
                GameNo = GetVal(dict, "game_no"),
                GameDate = GetVal(dict, "game_date"),
                StartTime = GetVal(dict, "start_time"),
                Matchup = GetVal(dict, "matchup"),
                TeamCode = GetVal(dict, "team_code"),
                TeamName = GetVal(dict, "team_name"),
                TeamScore = IntVal(dict, "team_score"),
                OpponentName = GetVal(dict, "opponent_name"),
                OpponentScore = IntVal(dict, "opponent_score"),
                PlayerNo = IntVal(dict, "player_no"),
                PlayerName = GetVal(dict, "player_name"),
                Status = GetVal(dict, "status"),
                IsStarter = IntVal(dict, "is_starter") == 1,
                IsCaptain = IntVal(dict, "is_captain") == 1,
                GamesListed = IntVal(dict, "games_listed"),
                GamesPlayed = IntVal(dict, "games_played"),
                Starts = IntVal(dict, "starts"),
                Minutes = GetVal(dict, "min"),
                TwoPtMA = GetVal(dict, "2p_ma"),
                ThreePtMA = GetVal(dict, "3p_ma"),
                FtMA = GetVal(dict, "ft_ma"),
                OffensiveRebounds = IntVal(dict, "or"),
                DefensiveRebounds = IntVal(dict, "dr"),
                TotalRebounds = IntVal(dict, "reb"),
                Assists = IntVal(dict, "ast"),
                Turnovers = IntVal(dict, "to"),
                Steals = IntVal(dict, "stl"),
                Blocks = IntVal(dict, "blk"),
                PersonalFouls = IntVal(dict, "pf"),
                FoulsDrawn = IntVal(dict, "fd"),
                Efficiency = IntVal(dict, "eff"),
                Points = IntVal(dict, "pts"),
                TeamOffReb = IntVal(dict, "team_or"),
                TeamDefReb = IntVal(dict, "team_dr"),
                TeamReb = IntVal(dict, "team_reb"),
                TeamPF = IntVal(dict, "team_pf"),
                TeamFD = IntVal(dict, "team_fd"),
            });
        }
        return rows;
    }

    private static string? GetVal(Dictionary<string, string> dict, string key) =>
        dict.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static int? IntVal(Dictionary<string, string> dict, string key)
    {
        var v = GetVal(dict, key);
        return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static BasketballExtractedRowDto MapSidecarRow(BasketballSidecarRow r) => new()
    {
        Granularity = r.Granularity, RowType = r.RowType, SourceFile = r.SourceFile,
        GameNo = r.GameNo, GameDate = r.GameDate, StartTime = r.StartTime, Matchup = r.Matchup,
        TeamCode = r.TeamCode, TeamName = r.TeamName,
        TeamScore = r.TeamScore, OpponentName = r.OpponentName, OpponentScore = r.OpponentScore,
        PlayerNo = r.PlayerNo, PlayerName = r.PlayerName, Status = r.Status,
        IsStarter = r.IsStarter == 1, IsCaptain = r.IsCaptain == 1,
        GamesListed = r.GamesListed, GamesPlayed = r.GamesPlayed, Starts = r.Starts,
        Minutes = r.Min, TwoPtMA = r.TwoPtMa, ThreePtMA = r.ThreePtMa, FtMA = r.FtMa,
        OffensiveRebounds = r.Or, DefensiveRebounds = r.Dr, TotalRebounds = r.Reb,
        Assists = r.Ast, Turnovers = r.To, Steals = r.Stl, Blocks = r.Blk,
        PersonalFouls = r.Pf, FoulsDrawn = r.Fd, Efficiency = r.Eff, Points = r.Pts,
        TeamOffReb = r.TeamOr, TeamDefReb = r.TeamDr, TeamReb = r.TeamReb,
        TeamPF = r.TeamPf, TeamFD = r.TeamFd,
    };

    private readonly record struct MadeAttempt(int Made, int Attempted);
    private readonly record struct FibaTeamSide(string Name, int Score, int Index);
    private sealed record PlayerLookup(Guid UserId, string Name, string Email);
    private sealed record JerseyPlayerLookup(Guid UserId, string Name, int JerseyNumber);
    private sealed record BasketballUploadSelection(
        List<BasketballExtractedRow> PlayerRows,
        BasketballExtractedRow? TeamTotalRow,
        BasketballExtractedRow? OpponentTeamTotalRow);
    private sealed record UploadedTeamGroup(
        string Key,
        List<BasketballExtractedRow> PlayerRows,
        BasketballExtractedRow? TeamTotalRow,
        int Score);

    // Sidecar response models for FastAPI integration
    private sealed class BasketballSidecarResponse
    {
        public List<BasketballSidecarRow> Rows { get; set; } = new();
        public int Count { get; set; }
    }

    private sealed class BasketballSidecarRow
    {
        public string? Granularity { get; set; }
        public string? RowType { get; set; }
        public string? SourceFile { get; set; }
        public string? GameNo { get; set; }
        public string? GameDate { get; set; }
        public string? StartTime { get; set; }
        public string? Matchup { get; set; }
        public string? TeamCode { get; set; }
        public string? TeamName { get; set; }
        public int? TeamScore { get; set; }
        public string? OpponentName { get; set; }
        public int? OpponentScore { get; set; }
        public int? PlayerNo { get; set; }
        public string? PlayerName { get; set; }
        public string? Status { get; set; }
        public int? IsStarter { get; set; }
        public int? IsCaptain { get; set; }
        public int? GamesListed { get; set; }
        public int? GamesPlayed { get; set; }
        public int? Starts { get; set; }
        public string? Min { get; set; }
        public string? TwoPtMa { get; set; }
        public string? ThreePtMa { get; set; }
        public string? FtMa { get; set; }
        public int? Or { get; set; }
        public int? Dr { get; set; }
        public int? Reb { get; set; }
        public int? Ast { get; set; }
        public int? To { get; set; }
        public int? Stl { get; set; }
        public int? Blk { get; set; }
        public int? Pf { get; set; }
        public int? Fd { get; set; }
        public int? Eff { get; set; }
        public int? Pts { get; set; }
        public int? TeamOr { get; set; }
        public int? TeamDr { get; set; }
        public int? TeamReb { get; set; }
        public int? TeamPf { get; set; }
        public int? TeamFd { get; set; }
    }
}
