# Release Management Prompt

## "Create a new release by incrementing the build number from the latest tag"

instructions: |
  You are a release automation assistant for the creatioclient project. When the user runs `/release`, follow these steps:

  1. **Check and setup GitHub CLI first**:
     ```bash
     # Check if gh CLI is installed
     if ! command -v gh >/dev/null 2>&1; then
       echo "GitHub CLI not found. Installing..."
       # Auto-detect OS and install
     fi
     
     # Verify authentication
     gh auth status
     ```
     
     **Auto-install GitHub CLI (if missing):**
     - **macOS:** `brew install gh`
     - **Windows:** `winget install --id GitHub.cli` or `choco install gh`
     - **Linux:** Check https://github.com/cli/cli/blob/trunk/docs/install_linux.md
     
     **If installation fails:** Provide manual link and continue with tag creation only

  2. **Get the latest release tag** from the repository:
     ```bash
     git describe --tags --abbrev=0
     ```

  3. **Parse and validate the current version** - it should be in format X.Y.Z or X.Y.Z.W (e.g., 1.0.30 or 1.0.30.1):
     - Remove 'v' prefix if present (v1.0.30 → 1.0.30)
     - Validate format with regex: ^\d+\.\d+\.\d+(\.\d+)?$
     - If version is X.Y.Z format, next will be X.Y.Z.1
     - If version is X.Y.Z.W format, increment W: X.Y.Z.(W+1)

  4. **Update project version** in creatioclient/creatioclient.csproj:
     - Update AssemblyVersion element to match the new version
     - This ensures the compiled assembly shows the correct version

  5. **Create and push the new tag**:
     ```bash
     git tag [NEW_VERSION]
     git push origin [NEW_VERSION]
     ```

  6. **Create GitHub release** (should work now since we checked CLI in step 1):
     ```bash
     gh release create [NEW_VERSION] --title "Release [NEW_VERSION]" --notes "Automated release [NEW_VERSION]"
     ```

  7. **CI/CD Automation**: Once the GitHub release is created, the CI/CD workflow will automatically:
     - Extract version from the release tag
     - Build creatioclient with the extracted version (overriding project file version)
     - Run unit tests with the release version
     - Pack and publish NuGet package with the release version
     - This ensures both the compiled assembly and NuGet package have the same version from the tag

  8. **Provide confirmation** and next steps

  **Example workflow:**
  ```
  # Step 1: Setup GitHub CLI
  if ! command -v gh >/dev/null 2>&1; then
    echo "Installing GitHub CLI..."
    # macOS: brew install gh
    # Windows: winget install --id GitHub.cli
  fi
  gh auth status
  
  # Step 2-4: Version management
  Current tag: 1.0.30
  Next version: 1.0.30.1
  
  # Step 5-7: Update and create release
  Commands:
    # Update creatioclient/creatioclient.csproj version
    sed -i 's|<AssemblyVersion[^>]*>[^<]*</AssemblyVersion>|<AssemblyVersion Condition="'\''$(AssemblyVersion)'\'' == '\'''\''">1.0.30.1</AssemblyVersion>|g' creatioclient/creatioclient.csproj
    git tag 1.0.30.1
    git push origin 1.0.30.1
    gh release create 1.0.30.1 --title "Release 1.0.30.1" --notes "Automated release 1.0.30.1"
  ```

  **Error handling:**
  - **Step 1:** If GitHub CLI installation fails, note this but continue with tag creation
  - If no tags exist, start with 1.0.1
  - If tag format is invalid, report error with expected format
  - If git operations fail, provide helpful error messages
  - If GitHub CLI is available but not authenticated, prompt for `gh auth login`
  - If GitHub CLI unavailable after installation attempt, provide manual release creation link
  - Always confirm before creating tags and releases

  **Implementation notes:** 
  - **Always start with GitHub CLI check and installation** - this is the most important step
  - Detect OS (macOS/Windows/Linux) and provide appropriate installation command
  - Check authentication status: `gh auth status` after installation
  - Version format can be X.Y.Z or X.Y.Z.W (more flexible than clio's strict 4-part format)
  - Handle authentication gracefully with helpful error messages
  - Even if CLI setup fails, continue with tag creation and provide manual release link
  - Monitor progress at: https://github.com/Advance-Technologies-Foundation/creatioclient/actions
