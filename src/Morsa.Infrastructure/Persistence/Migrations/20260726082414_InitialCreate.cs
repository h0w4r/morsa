using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Morsa.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ArtifactSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceUri = table.Column<string>(type: "TEXT", nullable: true),
                OriginalPath = table.Column<string>(type: "TEXT", nullable: true),
                StoredPath = table.Column<string>(type: "TEXT", nullable: false),
                Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                Size = table.Column<long>(type: "INTEGER", nullable: false),
                MimeType = table.Column<string>(type: "TEXT", nullable: true),
                Kind = table.Column<int>(type: "INTEGER", nullable: false),
                AcquiredAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ArtifactSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "DiscoveredResourceSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: true),
                Url = table.Column<string>(type: "TEXT", nullable: false),
                CanonicalUrl = table.Column<string>(type: "TEXT", nullable: false),
                ProviderId = table.Column<string>(type: "TEXT", nullable: false),
                Query = table.Column<string>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", nullable: true),
                Snippet = table.Column<string>(type: "TEXT", nullable: true),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                LastError = table.Column<string>(type: "TEXT", nullable: true),
                DiscoveredAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DiscoveredResourceSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "DnsObservationSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                RecordType = table.Column<string>(type: "TEXT", nullable: false),
                Value = table.Column<string>(type: "TEXT", nullable: false),
                Ttl = table.Column<uint>(type: "INTEGER", nullable: true),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                ObservedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DnsObservationSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "EntitySet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                Type = table.Column<string>(type: "TEXT", nullable: false),
                Value = table.Column<string>(type: "TEXT", nullable: false),
                NormalizedValue = table.Column<string>(type: "TEXT", nullable: false),
                Confidence = table.Column<double>(type: "REAL", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EntitySet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "EvidenceSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ArtifactId = table.Column<Guid>(type: "TEXT", nullable: false),
                ObservationId = table.Column<Guid>(type: "TEXT", nullable: true),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                Value = table.Column<string>(type: "TEXT", nullable: false),
                Location = table.Column<string>(type: "TEXT", nullable: true),
                ArtifactSha256 = table.Column<string>(type: "TEXT", nullable: false),
                CollectedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EvidenceSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FindingSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                ArtifactId = table.Column<Guid>(type: "TEXT", nullable: true),
                RuleId = table.Column<string>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: false),
                Severity = table.Column<int>(type: "INTEGER", nullable: false),
                Confidence = table.Column<double>(type: "REAL", nullable: false),
                Sensitive = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FindingSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MalwareObservationSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                ArtifactId = table.Column<Guid>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                Value = table.Column<string>(type: "TEXT", nullable: false),
                Severity = table.Column<string>(type: "TEXT", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MalwareObservationSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MetadataObservationSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ArtifactId = table.Column<Guid>(type: "TEXT", nullable: false),
                Category = table.Column<string>(type: "TEXT", nullable: false),
                OriginalValue = table.Column<string>(type: "TEXT", nullable: false),
                NormalizedValue = table.Column<string>(type: "TEXT", nullable: false),
                Extractor = table.Column<string>(type: "TEXT", nullable: false),
                ExtractorVersion = table.Column<string>(type: "TEXT", nullable: false),
                Location = table.Column<string>(type: "TEXT", nullable: true),
                Confidence = table.Column<double>(type: "REAL", nullable: false),
                ObservedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MetadataObservationSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "NetworkAttemptSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: true),
                TaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                ProxyEndpointId = table.Column<Guid>(type: "TEXT", nullable: true),
                Destination = table.Column<string>(type: "TEXT", nullable: false),
                Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                StatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                BytesReceived = table.Column<long>(type: "INTEGER", nullable: false),
                DurationMs = table.Column<double>(type: "REAL", nullable: false),
                RotationReason = table.Column<string>(type: "TEXT", nullable: true),
                AttemptedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NetworkAttemptSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PluginExecutionSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: true),
                PluginId = table.Column<string>(type: "TEXT", nullable: false),
                PluginVersion = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                FinishedAt = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PluginExecutionSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ProjectSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                RootPath = table.Column<string>(type: "TEXT", nullable: false),
                DefaultMode = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProjectSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ProviderRequestSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                ProviderId = table.Column<string>(type: "TEXT", nullable: false),
                Query = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                LastCursor = table.Column<string>(type: "TEXT", nullable: true),
                NextRetryAt = table.Column<long>(type: "INTEGER", nullable: true),
                CoverageTagsJson = table.Column<string>(type: "TEXT", nullable: true),
                LastError = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProviderRequestSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ProxyEndpointSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PoolId = table.Column<Guid>(type: "TEXT", nullable: false),
                Uri = table.Column<string>(type: "TEXT", nullable: false),
                Protocol = table.Column<int>(type: "INTEGER", nullable: false),
                DnsMode = table.Column<int>(type: "INTEGER", nullable: false),
                SecretRef = table.Column<string>(type: "TEXT", nullable: true),
                Weight = table.Column<int>(type: "INTEGER", nullable: false),
                TagsJson = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                SuccessCount = table.Column<long>(type: "INTEGER", nullable: false),
                FailureCount = table.Column<long>(type: "INTEGER", nullable: false),
                EwmaLatencyMs = table.Column<double>(type: "REAL", nullable: true),
                MaxConcurrency = table.Column<int>(type: "INTEGER", nullable: false),
                CooldownUntil = table.Column<long>(type: "INTEGER", nullable: true),
                LastCheckedAt = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProxyEndpointSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ProxyHealthSampleSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProxyEndpointId = table.Column<Guid>(type: "TEXT", nullable: false),
                Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                LatencyMs = table.Column<double>(type: "REAL", nullable: true),
                ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                ObservedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProxyHealthSampleSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ProxyLeaseSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: true),
                TaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                ProxyEndpointId = table.Column<Guid>(type: "TEXT", nullable: false),
                SessionKey = table.Column<string>(type: "TEXT", nullable: false),
                AcquiredAt = table.Column<long>(type: "INTEGER", nullable: false),
                ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                ReleasedAt = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProxyLeaseSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ProxyPoolSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                SelectionPolicy = table.Column<int>(type: "INTEGER", nullable: false),
                MaxRotations = table.Column<int>(type: "INTEGER", nullable: false),
                MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                CooldownSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                LeaseTtlSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                AllowDirectFallback = table.Column<bool>(type: "INTEGER", nullable: false),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProxyPoolSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "RelationSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                FromEntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                ToEntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                Type = table.Column<string>(type: "TEXT", nullable: false),
                EvidenceId = table.Column<Guid>(type: "TEXT", nullable: false),
                Confidence = table.Column<double>(type: "REAL", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RelationSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "RunSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                Command = table.Column<string>(type: "TEXT", nullable: false),
                Mode = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                CoverageStatus = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                FinishedAt = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RunSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ScopeEntrySet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                Value = table.Column<string>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                MaximumMode = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ScopeEntrySet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ServiceObservationSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                Host = table.Column<string>(type: "TEXT", nullable: false),
                Port = table.Column<int>(type: "INTEGER", nullable: false),
                Protocol = table.Column<string>(type: "TEXT", nullable: false),
                Banner = table.Column<string>(type: "TEXT", nullable: true),
                TlsSubject = table.Column<string>(type: "TEXT", nullable: true),
                TlsIssuer = table.Column<string>(type: "TEXT", nullable: true),
                Technology = table.Column<string>(type: "TEXT", nullable: true),
                ObservedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ServiceObservationSet", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "TaskSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                NextRetryAt = table.Column<long>(type: "INTEGER", nullable: true),
                LastErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                LastErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaskSet", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ArtifactSet_RunId_Sha256",
            table: "ArtifactSet",
            columns: new[] { "RunId", "Sha256" });

        migrationBuilder.CreateIndex(
            name: "IX_DiscoveredResourceSet_ProjectId_CanonicalUrl",
            table: "DiscoveredResourceSet",
            columns: new[] { "ProjectId", "CanonicalUrl" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_DnsObservationSet_RunId_Name_RecordType_Value",
            table: "DnsObservationSet",
            columns: new[] { "RunId", "Name", "RecordType", "Value" });

        migrationBuilder.CreateIndex(
            name: "IX_EntitySet_ProjectId_Type_NormalizedValue",
            table: "EntitySet",
            columns: new[] { "ProjectId", "Type", "NormalizedValue" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MalwareObservationSet_ArtifactId_Kind_Value",
            table: "MalwareObservationSet",
            columns: new[] { "ArtifactId", "Kind", "Value" });

        migrationBuilder.CreateIndex(
            name: "IX_MetadataObservationSet_ArtifactId",
            table: "MetadataObservationSet",
            column: "ArtifactId");

        migrationBuilder.CreateIndex(
            name: "IX_NetworkAttemptSet_AttemptedAt",
            table: "NetworkAttemptSet",
            column: "AttemptedAt");

        migrationBuilder.CreateIndex(
            name: "IX_ProjectSet_RootPath",
            table: "ProjectSet",
            column: "RootPath",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ProviderRequestSet_RunId_ProviderId_Query",
            table: "ProviderRequestSet",
            columns: new[] { "RunId", "ProviderId", "Query" });

        migrationBuilder.CreateIndex(
            name: "IX_ProxyEndpointSet_PoolId_Uri",
            table: "ProxyEndpointSet",
            columns: new[] { "PoolId", "Uri" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ProxyLeaseSet_SessionKey_ReleasedAt",
            table: "ProxyLeaseSet",
            columns: new[] { "SessionKey", "ReleasedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_ProxyPoolSet_Name",
            table: "ProxyPoolSet",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ScopeEntrySet_ProjectId_Kind_Value",
            table: "ScopeEntrySet",
            columns: new[] { "ProjectId", "Kind", "Value" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ServiceObservationSet_RunId_Host_Port_Protocol",
            table: "ServiceObservationSet",
            columns: new[] { "RunId", "Host", "Port", "Protocol" });

        migrationBuilder.CreateIndex(
            name: "IX_TaskSet_RunId_IdempotencyKey",
            table: "TaskSet",
            columns: new[] { "RunId", "IdempotencyKey" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ArtifactSet");

        migrationBuilder.DropTable(
            name: "DiscoveredResourceSet");

        migrationBuilder.DropTable(
            name: "DnsObservationSet");

        migrationBuilder.DropTable(
            name: "EntitySet");

        migrationBuilder.DropTable(
            name: "EvidenceSet");

        migrationBuilder.DropTable(
            name: "FindingSet");

        migrationBuilder.DropTable(
            name: "MalwareObservationSet");

        migrationBuilder.DropTable(
            name: "MetadataObservationSet");

        migrationBuilder.DropTable(
            name: "NetworkAttemptSet");

        migrationBuilder.DropTable(
            name: "PluginExecutionSet");

        migrationBuilder.DropTable(
            name: "ProjectSet");

        migrationBuilder.DropTable(
            name: "ProviderRequestSet");

        migrationBuilder.DropTable(
            name: "ProxyEndpointSet");

        migrationBuilder.DropTable(
            name: "ProxyHealthSampleSet");

        migrationBuilder.DropTable(
            name: "ProxyLeaseSet");

        migrationBuilder.DropTable(
            name: "ProxyPoolSet");

        migrationBuilder.DropTable(
            name: "RelationSet");

        migrationBuilder.DropTable(
            name: "RunSet");

        migrationBuilder.DropTable(
            name: "ScopeEntrySet");

        migrationBuilder.DropTable(
            name: "ServiceObservationSet");

        migrationBuilder.DropTable(
            name: "TaskSet");
    }
}
