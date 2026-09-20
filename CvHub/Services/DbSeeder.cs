using CvHub.Data;
using CvHub.Domain;
using CvHub.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Services;

/// <summary>
/// Seeds realistic demo application data so the app is fully populated out of
/// the box: attribute library, tags, positions (with required attributes, access
/// filters and tags), candidate profiles (attribute values + projects + pinned
/// profile attributes), CVs (with pre-selected projects), comments and likes.
///
/// Idempotent: re-running only adds rows that are missing, so it is safe to run
/// on every startup. Complements <see cref="IdentitySeeder"/> which owns roles,
/// demo users and the built-in "Me" attributes.
/// </summary>
public class DbSeeder(UserManager<ApplicationUser> users, ApplicationDbContext db)
{
    private static readonly DateTimeOffset Epoch = new(2025, 01, 01, 00, 00, 00, TimeSpan.Zero);

    private const string RecruiterEmail = "recruiter@cvhub.local";
    private const string CandidateEmail = "candidate@cvhub.local";
    private const string Candidate2Email = "candidate2@cvhub.local";

    public async Task SeedAsync()
    {
        // Demo users are created by IdentitySeeder; look them up by email.
        var carl   = await users.FindByEmailAsync(CandidateEmail);
        var carla  = await users.FindByEmailAsync(Candidate2Email);
        var rachel = await users.FindByEmailAsync(RecruiterEmail);

        var attrs = await SeedAttributesAsync();
        var tags  = await SeedTagsAsync();
        var positionsByName = await SeedPositionsAsync(attrs, tags);

        // Candidate data only makes sense if the demo users exist.
        if (carl is not null && carla is not null)
        {
            await SeedCandidateProfilesAsync(carl, carla, attrs);
            var projects = await SeedProjectsAsync(carl, carla, tags);
            await SeedProfileAttributesAsync(carl, carla, attrs);
            await SeedCvsAsync(carl, carla, positionsByName, projects);
            await SeedRecentAttributesAsync(carl, carla, attrs);
            await SeedLikesAsync(carla, positionsByName, carl);
        }

        if (rachel is not null)
            await SeedCommentsAsync(rachel, positionsByName);

        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------ attributes
    private async Task<Dictionary<string, AttributeDef>> SeedAttributesAsync()
    {
        // (name, category, type, options, minLength, maxLength, regex, minValue, maxValue)
        var defs = new (string, string, AttributeType, string?, int?, int?, string?, double?, double?)[]
        {
            ("Primary Skills",        AttributeCategories.All[1],  AttributeType.Text,       ".NET\nC#\nReact\nPostgreSQL\nDocker\nAWS\nKubernetes\nTypeScript\nF#\nKafka\nRedis", null, null, null, null, null),
            ("Languages",             AttributeCategories.All[6],  AttributeType.OneOfMany,  "English\nSpanish\nFrench\nGerman\nJapanese\nUzbek", null, null, null, null, null),
            ("Years of Experience",   AttributeCategories.All[5],  AttributeType.Numeric,      null, null, null, null, 0, 50),
            ("Education",             AttributeCategories.All[4],  AttributeType.Text,         null, null, null, null, null, null),
            ("Certifications",        AttributeCategories.All[0],  AttributeType.Text,         null, null, null, null, null, null),
            ("GitHub URL",            AttributeCategories.All[7],  AttributeType.String,       null, null, 200, @"^https?://\S+$", null, null),
            ("Portfolio URL",         AttributeCategories.All[7],  AttributeType.String,       null, null, 200, @"^https?://\S+$", null, null),
            ("LinkedIn URL",          AttributeCategories.All[7],  AttributeType.String,       null, null, 200, @"^https?://\S+$", null, null),
            ("Available Remotely",    AttributeCategories.All[7],  AttributeType.Boolean,      null, null, null, null, null, null),
        };

        var map = new Dictionary<string, AttributeDef>();
        // Pre-load existing (incl. built-in) attributes so profile values can reference them.
        foreach (var a in await db.Attributes.Where(x => !x.IsDeleted).ToListAsync())
            map[a.Name] = a;

        foreach (var (name, category, type, options, minLen, maxLen, regex, minVal, maxVal) in defs)
        {
            if (!map.TryGetValue(name, out var existing))
            {
                existing = new AttributeDef
                {
                    Name = name, Category = category, Type = type, Options = options,
                    MinLength = minLen, MaxLength = maxLen, RegexPattern = regex,
                    MinValue = minVal, MaxValue = maxVal,
                    IsBuiltIn = false, CreatedAt = Epoch,
                };
                db.Attributes.Add(existing);
                await db.SaveChangesAsync();
            }
            map[name] = existing;
        }
        return map;
    }

    // --------------------------------------------------------------- tags
    private async Task<Dictionary<string, Tag>> SeedTagsAsync()
    {
        var names = new[]
        {
            "React", "TypeScript", ".NET", "C#", "SQL", "PostgreSQL",
            "Docker", "AWS", "Kubernetes", "Tailwind CSS", "Azure", "Python",
            "Git", "Redis", "F#", "Kafka",
        };

        var map = new Dictionary<string, Tag>();
        foreach (var name in names)
        {
            var tag = await db.Tags.FirstOrDefaultAsync(t => t.Name == name);
            if (tag is null)
            {
                tag = new Tag { Name = name };
                db.Tags.Add(tag);
                await db.SaveChangesAsync();
            }
            map[name] = tag;
        }
        return map;
    }

    // ----------------------------------------------------------- positions
    // (title, shortDesc, company, level, access, requiredAttrNames, filterOperator/filterValue?, tagNames)
    private async Task<Dictionary<string, Position>> SeedPositionsAsync(
        Dictionary<string, AttributeDef> attrs, Dictionary<string, Tag> tags)
    {
        var defs = new[]
        {
            ("Senior .NET Backend Engineer",
             "We are building the next-generation payment platform on .NET 8 and PostgreSQL.",
             "TechCorp", "Senior", PositionAccess.Restricted,
             new[] { "Primary Skills", "Years of Experience", "Languages", "GitHub URL" },
             (FilterOperator.GreaterOrEqual, "3"),
             new[] { ".NET", "C#", "SQL", "PostgreSQL", "Docker", "AWS" }),
            ("Frontend React Engineer",
             "Join a fast-moving startup shipping a React + TypeScript SaaS product daily.",
             "StartupXYZ", "Middle", PositionAccess.Public,
             new[] { "Primary Skills", "Years of Experience", "Languages", "Portfolio URL" },
             (FilterOperator.GreaterOrEqual, "2"),
             new[] { "React", "TypeScript", "Tailwind CSS", "Git" }),
            ("Cloud / DevOps Engineer",
             "Own our multi-cloud Kubernetes + AWS platform. Terraform and on-call rotation.",
             "InfraCo", "Senior", PositionAccess.Restricted,
             new[] { "Primary Skills", "Years of Experience", "Languages", "GitHub URL", "Available Remotely" },
             (FilterOperator.GreaterOrEqual, "4"),
             new[] { "AWS", "Kubernetes", "Docker", "Azure", "Git" }),
            ("Data Engineer",
             "Design and maintain the data warehouse (PostgreSQL) and streaming pipelines.",
             "DataCorp", "Senior", PositionAccess.Public,
             new[] { "Primary Skills", "Years of Experience", "Languages", "GitHub URL" },
             (FilterOperator.GreaterOrEqual, "3"),
             new[] { "Python", "PostgreSQL", "AWS", "Git", "Kafka" }),
        };

        var map = new Dictionary<string, Position>();
        var sort = 0;
        foreach (var (title, desc, company, level, access, reqAttrNames, (op, val), tagNames) in defs)
        {
            var pos = await db.Positions.FirstOrDefaultAsync(p => p.Title == title && !p.IsDeleted);
            if (pos is null)
            {
                pos = new Position
                {
                    Title = title, ShortDescription = desc, Company = company, Level = level,
                    Access = access, MaxProjects = 5,
                    CreatedAt = Epoch, UpdatedAt = Epoch,
                };
                db.Positions.Add(pos);
                await db.SaveChangesAsync();
            }
            map[title] = pos;

            // Required attributes (+ presentation section/sort).
            foreach (var attrName in reqAttrNames)
            {
                var attrId = attrs[attrName].Id;
                if (!await db.PositionAttributes.AnyAsync(pa => pa.PositionId == pos.Id && pa.AttributeId == attrId))
                    db.PositionAttributes.Add(new PositionAttribute
                    {
                        PositionId = pos.Id, AttributeId = attrId,
                        Required = true, SortOrder = sort++, Section = "Requirements",
                    });
            }

            // Access filter: only Restricted positions enforce a Years-of-Experience threshold.
            var yearsAttr = attrs["Years of Experience"];
            if (pos.Access == PositionAccess.Restricted &&
                !await db.PositionFilters.AnyAsync(pf => pf.PositionId == pos.Id && pf.AttributeId == yearsAttr.Id))
                db.PositionFilters.Add(new PositionFilter
                {
                    PositionId = pos.Id, AttributeId = yearsAttr.Id,
                    Operator = op, Value = val,
                });

            // Position tags (drive CV project selection).
            foreach (var tagName in tagNames)
            {
                var tagId = tags[tagName].Id;
                if (!await db.PositionTags.AnyAsync(pt => pt.PositionId == pos.Id && pt.TagId == tagId))
                    db.PositionTags.Add(new PositionTag { PositionId = pos.Id, TagId = tagId });
            }
            await db.SaveChangesAsync();
        }
        return map;
    }

    // ----------------------------------------------------- candidate profiles
    private async Task SeedCandidateProfilesAsync(ApplicationUser carl, ApplicationUser carla,
        Dictionary<string, AttributeDef> attrs)
    {
        var carolValues = new[]
        {
            StrVal(attrs, carl.Id,  "First Name",   "Carl"),
            StrVal(attrs, carl.Id,  "Last Name",    "Candidate"),
            StrVal(attrs, carl.Id,  "Location",     "Almaty, Kazakhstan"),
            StrVal(attrs, carl.Id,  "Headline",      "Full-Stack Engineer (.NET + React)"),
            TxtVal(attrs, carl.Id,  "About Me",       "# Hi - I build scalable .NET backends and React frontends."),
            TxtVal(attrs, carl.Id,  "Primary Skills", ".NET\nC#\nReact\nPostgreSQL\nDocker\nAWS\nKafka\nRedis"),
            OptVal(attrs, carl.Id,  "Languages",      "English"),
            NumVal(attrs, carl.Id,  "Years of Experience", 4),
            StrVal(attrs, carl.Id,  "GitHub URL",     "https://github.com/carlcandev"),
            TxtVal(attrs, carl.Id,  "Certifications",   "AWS Certified Developer - Associate"),
            BoolVal(attrs, carl.Id, "Available Remotely", true),
            TxtVal(attrs, carl.Id,  "Education",      "BSc Computer Science, 2016-2020, Nazarbayev University"),
        };
        var carlaValues = new[]
        {
            StrVal(attrs, carla.Id, "First Name",   "Carla"),
            StrVal(attrs, carla.Id, "Last Name",    "Candidate"),
            StrVal(attrs, carla.Id, "Location",     "Berlin, Germany"),
            StrVal(attrs, carla.Id, "Headline",      "Frontend Engineer"),
            TxtVal(attrs, carla.Id, "About Me",    "# Hi - I craft interactive web apps with React and TypeScript."),
            TxtVal(attrs, carla.Id, "Primary Skills", "React\nTypeScript\nTailwind CSS\n.NET\nPostgreSQL"),
            OptVal(attrs, carla.Id, "Languages",     "English"),
            NumVal(attrs, carla.Id, "Years of Experience", 3),
            StrVal(attrs, carla.Id, "Portfolio URL",  "https://carla.dev"),
            StrVal(attrs, carla.Id, "GitHub URL",    "https://github.com/carlaconnor"),
            BoolVal(attrs, carla.Id, "Available Remotely", true),
            TxtVal(attrs, carla.Id, "Education",     "MSc Software Engineering, 2017-2019, TU Berlin"),
        };

        foreach (var v in carolValues.Concat(carlaValues))
        {
            var existing = await db.AttributeValues.FirstOrDefaultAsync(
                x => x.UserId == v.UserId && x.AttributeId == v.AttributeId);
            if (existing is null)
                db.AttributeValues.Add(v);
        }
        await db.SaveChangesAsync();
    }

    // ----------------------------------------------------------- projects
    private async Task<Dictionary<string, Project>> SeedProjectsAsync(
        ApplicationUser carl, ApplicationUser carla, Dictionary<string, Tag> tags)
    {
        var defs = new[]
        {
            ("E-Commerce Platform",   carl,  new[] { ".NET", "C#", "SQL", "PostgreSQL", "Docker", "AWS" }),
            ("Collab Task Manager",   carl,  new[] { "React", "TypeScript", ".NET", "PostgreSQL" }),
            ("Stock Feed Service",    carl,  new[] { ".NET", "C#", "Kafka", "Redis" }),
            ("Analytics Dashboard",   carla, new[] { "React", "TypeScript", ".NET", "PostgreSQL", "Docker" }),
            ("Design System Library", carla, new[] { "React", "TypeScript", "Tailwind CSS" }),
            ("API Gateway",           carla, new[] { ".NET", "C#", "Azure", "Docker" }),
        };

        var map = new Dictionary<string, Project>();
        var sort = 0;
        foreach (var (title, owner, tagNames) in defs)
        {
            var p = await db.Projects.FirstOrDefaultAsync(x => x.Name == title && x.UserId == owner.Id);
            if (p is null)
            {
                p = new Project
                {
                    Name = title, Description = $"Demo project: {title}.",
                    PeriodStart = new DateOnly(2024, 03, 01), PeriodEnd = null,
                    UserId = owner.Id, SortOrder = sort++, CreatedAt = Epoch,
                };
                db.Projects.Add(p);
                await db.SaveChangesAsync();
            }
            map[title] = p;
            foreach (var tagName in tagNames)
            {
                var tagId = tags[tagName].Id;
                if (!await db.ProjectTags.AnyAsync(pt => pt.ProjectId == p.Id && pt.TagId == tagId))
                    db.ProjectTags.Add(new ProjectTag { ProjectId = p.Id, TagId = tagId });
            }
        }
        await db.SaveChangesAsync();
        return map;
    }

    // ---------------------------------------------- pinned profile attributes
    private async Task SeedProfileAttributesAsync(ApplicationUser carl, ApplicationUser carla,
        Dictionary<string, AttributeDef> attrs)
    {
        var pinSets = new (ApplicationUser user, string[] names)[]
        {
            (carl,  new[] { "First Name", "Last Name", "Location", "Headline", "About Me",
                            "Primary Skills", "Languages", "Years of Experience", "GitHub URL",
                            "Available Remotely" }),
            (carla, new[] { "First Name", "Last Name", "Location", "Headline", "About Me",
                            "Primary Skills", "Languages", "Years of Experience", "Portfolio URL",
                            "GitHub URL", "Available Remotely" }),
        };
        foreach (var (user, names) in pinSets)
        {
            var sort = 0;
            foreach (var name in names)
            {
                var attrId = attrs[name].Id;
                if (!await db.ProfileAttributes.AnyAsync(pa => pa.UserId == user.Id && pa.AttributeId == attrId))
                    db.ProfileAttributes.Add(new ProfileAttribute { UserId = user.Id, AttributeId = attrId, SortOrder = sort++ });
            }
        }
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------- CVs
    private async Task SeedCvsAsync(ApplicationUser carl, ApplicationUser carla,
        Dictionary<string, Position> positions, Dictionary<string, Project> projects)
    {
        // (candidate, positionTitle, status, projectTitles)
        var cvDefs = new[]
        {
            (carl,  "Senior .NET Backend Engineer", CvStatus.Published,
             new[] { "E-Commerce Platform", "Collab Task Manager", "Stock Feed Service" }),
            (carl,  "Cloud / DevOps Engineer", CvStatus.Draft,
             new[] { "E-Commerce Platform" }),
            (carla, "Frontend React Engineer", CvStatus.Published,
             new[] { "Analytics Dashboard", "Design System Library" }),
            (carla, "Data Engineer", CvStatus.Draft,
             new[] { "Analytics Dashboard" }),
        };
        foreach (var (candidate, positionTitle, status, projectTitles) in cvDefs)
        {
            var pos = positions[positionTitle];
            var cv = await db.Cvs.FirstOrDefaultAsync(c => c.PositionId == pos.Id && c.UserId == candidate.Id && !c.IsDeleted);
            if (cv is null)
            {
                cv = new Cv
                {
                    PositionId = pos.Id, UserId = candidate.Id,
                    Status = status, CreatedAt = Epoch,
                    PublishedAt = status == CvStatus.Published ? Epoch : null,
                };
                db.Cvs.Add(cv);
                await db.SaveChangesAsync();
            }
            foreach (var pt in projectTitles)
            {
                if (projects.TryGetValue(pt, out var project) &&
                    !await db.CvProjects.AnyAsync(cp => cp.CvId == cv.Id && cp.ProjectId == project.Id))
                    db.CvProjects.Add(new CvProject { CvId = cv.Id, ProjectId = project.Id });
            }
        }
        await db.SaveChangesAsync();
    }

    // ----------------------------------------------------------- comments
    private async Task SeedCommentsAsync(ApplicationUser rachel, Dictionary<string, Position> positions)
    {
        foreach (var title in new[] { "Frontend React Engineer", "Data Engineer" })
        {
            var pos = positions[title];
            if (!await db.Comments.AnyAsync(c => c.PositionId == pos.Id && c.UserId == rachel.Id))
                db.Comments.Add(new Comment
                {
                    PositionId = pos.Id, UserId = rachel.Id,
                    Body = title == "Frontend React Engineer"
                        ? "We use React + TypeScript daily - excited to see your Tailwind CSS work."
                        : "Design and maintain the data warehouse (PostgreSQL).",
                    CreatedAt = Epoch,
                });
        }
        await db.SaveChangesAsync();
    }

    // -------------------------------------------------------------- likes
    private async Task SeedLikesAsync(ApplicationUser carla, Dictionary<string, Position> positions, ApplicationUser carl)
    {
        var netPos = positions["Senior .NET Backend Engineer"];
        var netCv = await db.Cvs.FirstOrDefaultAsync(c => c.PositionId == netPos.Id && c.UserId == carl.Id && !c.IsDeleted);
        if (netCv is not null && !await db.Likes.AnyAsync(l => l.CvId == netCv.Id && l.UserId == carla.Id))
            db.Likes.Add(new CvLike { CvId = netCv.Id, UserId = carla.Id, CreatedAt = Epoch });
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------ recent attrs
    private async Task SeedRecentAttributesAsync(ApplicationUser carl, ApplicationUser carla,
        Dictionary<string, AttributeDef> attrs)
    {
        var recents = new[]
        {
            (carl,  new[] { "Primary Skills", "Years of Experience", "GitHub URL", "Languages" }),
            (carla, new[] { "Primary Skills", "Portfolio URL", "Years of Experience", "Languages" }),
        };
        foreach (var (user, names) in recents)
        {
            var ts = Epoch;
            foreach (var name in names)
            {
                var attrId = attrs[name].Id;
                if (!await db.RecentAttributes.AnyAsync(r => r.UserId == user.Id && r.AttributeId == attrId))
                    db.RecentAttributes.Add(new RecentAttribute { UserId = user.Id, AttributeId = attrId, UsedAt = ts });
                ts = ts.AddMinutes(1);
            }
        }
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------- builders
    private static AttributeValue StrVal(Dictionary<string, AttributeDef> attrs, string userId, string name, string value) =>
        new() { UserId = userId, AttributeId = attrs[name].Id, StringValue = value };

    private static AttributeValue OptVal(Dictionary<string, AttributeDef> attrs, string userId, string name, string value) =>
        new() { UserId = userId, AttributeId = attrs[name].Id, OptionValue = value };

    private static AttributeValue TxtVal(Dictionary<string, AttributeDef> attrs, string userId, string name, string value) =>
        new() { UserId = userId, AttributeId = attrs[name].Id, TextValue = value };

    private static AttributeValue NumVal(Dictionary<string, AttributeDef> attrs, string userId, string name, double value) =>
        new() { UserId = userId, AttributeId = attrs[name].Id, NumericValue = value };

    private static AttributeValue BoolVal(Dictionary<string, AttributeDef> attrs, string userId, string name, bool value) =>
        new() { UserId = userId, AttributeId = attrs[name].Id, BoolValue = value };
}
