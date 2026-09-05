TFM      := netstandard2.1
CONFIG   := Debug
DLL      := VGMissionJournal.dll

BUILDDIR := VGMissionJournal/bin/$(CONFIG)/$(TFM)
BUILDDLL := $(BUILDDIR)/$(DLL)

# WSL path to the game install — adjust if Steam lives elsewhere.
GAME_DIR   := /mnt/c/Program Files (x86)/Steam/steamapps/common/Vanguard Galaxy
PLUGIN_DIR := $(GAME_DIR)/BepInEx/plugins
VGMISSIONJOURNAL_DIR := $(PLUGIN_DIR)/VGMissionJournal

# Current owner-local metadata; older sibling stubs hide signature drift.
PUBLICIZER ?= assembly-publicizer
VGAPI_DLL ?= ../vanguard-galaxy-api/VGModAPI.Abstractions/bin/Release/netstandard2.1/VGModAPI.Abstractions.dll

DOTNET ?= $(shell command -v dotnet 2>/dev/null || echo /tmp/dnsdk/dotnet/dotnet)

# Our test project targets net8.0, but CI/dev boxes frequently run a newer
# .NET SDK (10.x on this host). LatestMajor tells the host to roll forward
# past unavailable majors so `dotnet test` keeps working on single-runtime
# hosts without pinning a specific SDK.
export DOTNET_ROLL_FORWARD := LatestMajor

.PHONY: all build link-asm refresh-asm link-api deploy clean test package

all: build

link-asm:
	@test -f VGMissionJournal/lib/Assembly-CSharp.dll || { echo 'Run make refresh-asm with the current owner-installed game and assembly-publicizer.'; exit 1; }

refresh-asm:
	$(PUBLICIZER) --strip "$(GAME_DIR)/VanguardGalaxy_Data/Managed/Assembly-CSharp.dll" -o .local-reference
	@mkdir -p VGMissionJournal/lib
	ln -sfn "$(CURDIR)/.local-reference/Assembly-CSharp-publicized.dll" VGMissionJournal/lib/Assembly-CSharp.dll

link-api:
	@test -f "$(VGAPI_DLL)" || { echo 'Build the API Release package first, or set VGAPI_DLL to its Abstractions DLL.'; exit 1; }
	@mkdir -p VGMissionJournal/lib
	@ln -sf "$(abspath $(VGAPI_DLL))" VGMissionJournal/lib/VGModAPI.Abstractions.dll

build: link-asm link-api
	DOTNET_ROOT=$(dir $(DOTNET)) $(DOTNET) build VGMissionJournal/VGMissionJournal.csproj -c $(CONFIG)

test: link-asm link-api
	python3 tools/test_package.py
	DOTNET_ROOT=$(dir $(DOTNET)) $(DOTNET) test VGMissionJournal.Tests/VGMissionJournal.Tests.csproj -c $(CONFIG)

package: build
	python3 tools/package.py --configuration $(CONFIG)

deploy: package
	@test -d "$(PLUGIN_DIR)" || { echo "BepInEx plugins dir not found at $(PLUGIN_DIR)" ; exit 1 ; }
	@mkdir -p "$(VGMISSIONJOURNAL_DIR)"
	# Only owned plugin + Newtonsoft; never copy API/Unity/game references.
	cp "$(BUILDDIR)/VGMissionJournal.dll" "$(BUILDDIR)/Newtonsoft.Json.dll" "$(VGMISSIONJOURNAL_DIR)/"
	@if [ -f "$(BUILDDIR)/VGMissionJournal.pdb" ]; then cp "$(BUILDDIR)/VGMissionJournal.pdb" "$(VGMISSIONJOURNAL_DIR)/"; fi
	@echo "Deployed $(shell ls $(BUILDDIR)/*.dll | wc -l) DLL(s) to $(VGMISSIONJOURNAL_DIR)"

clean:
	-$(DOTNET) clean VGMissionJournal/VGMissionJournal.csproj
	rm -rf VGMissionJournal/bin VGMissionJournal/obj VGMissionJournal.Tests/bin VGMissionJournal.Tests/obj dist/
