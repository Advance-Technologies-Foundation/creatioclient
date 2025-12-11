# GitHub Copilot Custom Instructions for creatioclient Project

## Project Context
This is the creatioclient project - a .NET 8 client library for Creatio (formerly bpm'online) REST API. The project includes support for HTTP requests, file uploads, authentication, and WebSocket communication.

## Available Commands

### `/release` - Release Management
Automates version management and release creation for NuGet publishing.

**Usage:** `/release` followed by release type or specific instructions
- Semantic versioning support (X.Y.Z or X.Y.Z.W format)
- Automated GitHub release creation
- Integration with CI/CD pipelines for NuGet package publishing
- Cross-platform support (macOS, Windows, Linux)

**Key Features:**
- **GitHub CLI Integration**: Auto-checks and installs `gh` CLI if missing
- **Automatic Version Increment**: Increments build number or adds build component
- **Project File Update**: Updates AssemblyVersion in creatioclient.csproj
- **Git Tag Management**: Creates and pushes release tags
- **GitHub Release Creation**: Publishes releases with automated notes
- **CI/CD Automation**: Triggers NuGet publishing workflow automatically

**Version Format:**
- Uses semantic versioning: X.Y.Z (e.g., 1.0.30 → 1.0.31)
- Increments patch version (Z) by 1
- First release starts at `1.0.1`

**Usage Examples:**
- `/release` - Start the release wizard
- `/release what should be the next version?` - Interactive version discussion
- `/release publish 1.0.31` - Create specific version release

**Automated Steps:**
1. Check and auto-install GitHub CLI (if needed)
2. Fetch latest tag from repository
3. Calculate next version number
4. Update AssemblyVersion in creatioclient/creatioclient.csproj
5. Create git tag and push to repository
6. Create GitHub release using gh CLI
7. Trigger CI/CD workflow for NuGet publishing

**What Happens After Release:**
- GitHub Actions workflow `.github/workflows/release-to-nuget.yml` automatically:
  - Extracts version from release tag
  - Runs unit tests (NUnit)
  - Builds creatioclient package in Release configuration
  - Packs NuGet package
  - Publishes to NuGet.org (api.nuget.org)
- Package becomes available immediately: https://www.nuget.org/packages/creatio.client/

## Development Guidelines

### Project Structure
```
creatioclient/
  creatioclient/
    creatioclient.csproj          # Main project file
    CreatioClient.cs              # Primary client class
    ICreatioClient.cs             # Client interface
    Dto/                          # Data transfer objects
  creatioclient.example/          # Example usage
```

### Key Technologies
- **.NET 8.0** - Target framework
- **NUnit 4.4.0** - Unit testing framework
- **FluentAssertions** - Assertion library
- **System.IO.Abstractions** - File system abstraction
- **Newtonsoft.Json** - JSON serialization
- **HTTP Client** - REST API communication
- **WebSocket** - Real-time communication support (SignalR)

### Coding Standards

#### Version Management
- Use semantic versioning: `X.Y.Z` or `X.Y.Z.W` format
- AssemblyVersion updated via git tag during release
- Version defined in `creatioclient/creatioclient.csproj`
- Default version for local development: Last released version (e.g., 1.0.30)

#### File Organization
- Source code in `creatioclient/` directory
- Data Transfer Objects in `creatioclient/Dto/`
- Example implementations in `creatioclient.example/`
- Tests will be configured separately (currently uses NUnit framework)

#### Code Style
- Microsoft C# naming conventions
- Follow existing code patterns in `CreatioClient.cs`
- Use `ICreatioClient` interface for public APIs
- Proper XML documentation comments for public members

### Testing
- Tests use **NUnit 4.4.0** framework
- FluentAssertions for readable assertions
- File system operations use abstraction layer
- Example test structure (when tests are added):
  ```csharp
  [Test]
  [Description("Describes what the test validates")]
  public void TestMethod_Scenario_ExpectedResult()
  {
    // Arrange
    var client = new CreatioClient(baseUri);
    
    // Act
    var result = await client.SomeMethodAsync();
    
    // Assert
    result.Should().NotBeNull("because the method should return a value");
  }
  ```

### Dependencies
- Use NuGet package references in `.csproj`
- No external scripts or PowerShell for building
- `dotnet` CLI for all build operations

### Package Publishing
- NuGet Package ID: `creatio.client`
- Package published to: https://www.nuget.org/packages/creatio.client/
- Requires secret: `CREATIOCLIENT_NUGET_API_KEY` (GitHub secret)
- Published automatically via GitHub Actions on release

## CI/CD Integration

### Workflows
- `.github/workflows/release-to-nuget.yml` - Publishes releases to NuGet.org
  - Triggered: On GitHub release published
  - Steps: Test → Build → Pack → Publish

### Secrets Required
- `CREATIOCLIENT_NUGET_API_KEY` - NuGet API key for publishing

### GitHub Releases
- Created automatically via `/release` command or manual git tag
- Includes version number in title and notes
- Triggers NuGet publishing workflow

## Common Workflows

### Releasing a New Version
1. Run `/release` command in GitHub Copilot chat
2. Follow the prompts to confirm version bump
3. GitHub Actions automatically publishes to NuGet
4. Package available at: https://www.nuget.org/packages/creatio.client/

### Installing Latest Release
```bash
dotnet add package creatio.client
```

Or specify version:
```bash
dotnet add package creatio.client --version 1.0.30.1
```

### Verifying Release
- GitHub Releases: https://github.com/Advance-Technologies-Foundation/creatioclient/releases
- GitHub Actions: https://github.com/Advance-Technologies-Foundation/creatioclient/actions
- NuGet Package: https://www.nuget.org/packages/creatio.client/

## Communication Style

### When Providing Release Guidance
- Confirm current version and calculate next version clearly
- Show the commands that will be executed
- Explain what happens in CI/CD pipeline
- Provide links for monitoring progress
- Offer help with troubleshooting if needed

### When Discussing Development
- Reference project structure clearly
- Use Microsoft C# naming conventions
- Suggest tests following NUnit patterns
- Keep explanations practical and example-driven

## Error Handling

### Common Issues and Solutions

| Issue | Cause | Solution |
|-------|-------|----------|
| "gh: command not found" | GitHub CLI not installed | `/release` command will auto-install it |
| "Not authenticated" | GitHub CLI not logged in | Run `gh auth login` in terminal |
| "Tag already exists" | Version number already released | Use different version or check existing tags |
| "Build failed" | Project configuration issue | Run `dotnet build creatioclient/creatioclient.csproj -c Release` locally |
| "NuGet publish failed" | Missing API key secret | Verify `CREATIOCLIENT_NUGET_API_KEY` in repo settings |

## Additional Resources

- **creatioclient Repository**: https://github.com/Advance-Technologies-Foundation/creatioclient
- **creatioclient NuGet Package**: https://www.nuget.org/packages/creatio.client/
- **Creatio Official Documentation**: https://academy.creatio.com/
- **GitHub CLI Documentation**: https://cli.github.com/manual
- **.NET 8 Documentation**: https://learn.microsoft.com/en-us/dotnet/

## Release Best Practices

1. **Before Release**:
   - Ensure all PRs merged to main branch
   - Verify unit tests pass locally
   - Update any relevant documentation

2. **During Release** (via `/release` command):
   - Confirm version number is correct
   - Review changelog if available
   - Proceed with release confirmation

3. **After Release**:
   - Verify package published to NuGet
   - Test installation: `dotnet add package creatio.client`
   - Announce release to team/community
   - Update documentation with new features/fixes

4. **Troubleshooting**:
   - Monitor GitHub Actions workflow: https://github.com/Advance-Technologies-Foundation/creatioclient/actions
   - Check NuGet package page for processing time
   - If issues occur, refer to error handling section above
