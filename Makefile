# Makefile for Jplus (JARVIS for Claude Code, Linux edition)
# Requires the .NET 10 SDK (./dotnet-install.sh --channel 10.0 installs it to ~/.dotnet)
# and Node/npm the first time (the build runs `npm ci && npm run build` in frontend/
# when frontend/dist is missing). Override variables on the command line, e.g.:
#   make publish RID=linux-arm64
#   make build CONFIG=Debug

# Pick a dotnet whose SDK can target net10 (major version >= 10), checking the per-user
# dotnet-install.sh location (~/.dotnet) then PATH - this skips an older system SDK
# (e.g. /usr/bin/dotnet 6.x). Override with: make <target> DOTNET=/path/to/dotnet
DOTNET  ?= $(shell best=dotnet; for c in "$(HOME)/.dotnet/dotnet" "$$(command -v dotnet 2>/dev/null)"; do [ -x "$$c" ] || continue; "$$c" --list-sdks 2>/dev/null | grep -qE '^[1-9][0-9]+\.' && { best="$$c"; break; }; done; echo "$$best")
PROJECT := Jplus.csproj
CONFIG  ?= Release
PREFIX  ?= $(HOME)/.local
APPDIR  := $(PREFIX)/lib/jplus

# Auto-detect the host architecture so 'make publish' produces a native binary.
# Override on the command line, e.g.  make publish RID=linux-x64
UNAME_M := $(shell uname -m)
ifeq ($(UNAME_M),x86_64)
  RID ?= linux-x64
else ifeq ($(UNAME_M),aarch64)
  RID ?= linux-arm64
else ifeq ($(UNAME_M),arm64)
  RID ?= linux-arm64
else
  RID ?= linux-x64
endif

.DEFAULT_GOAL := build
.PHONY: all help restore build run release publish package install service uninstall clean

all: build

help:
	@echo "Jplus build targets:"
	@echo "  make build      - build ($(CONFIG)) for $(RID)"
	@echo "  make run        - build ($(CONFIG)) & run the server in this terminal"
	@echo "  make release    - build in Release configuration"
	@echo "  make publish    - self-contained single-file binary -> dist/$(RID)/Jplus"
	@echo "  make package    - publish + assemble dist/jplus-$(RID).tar.gz"
	@echo "  make install    - publish then install per-user: $(APPDIR)/Jplus, $(PREFIX)/bin/jplus"
	@echo "  make service    - install, then register the systemd user unit (jplus install)"
	@echo "  make uninstall  - remove the systemd unit and the binary (keeps $(APPDIR)/data and .env)"
	@echo "  make clean      - remove dist/, bin/, obj/"
	@echo ""
	@echo "Variables: CONFIG=$(CONFIG)  RID=$(RID)  PREFIX=$(PREFIX)  DOTNET=$(DOTNET)"

restore:
	$(DOTNET) restore $(PROJECT) -r $(RID)

build:
	$(DOTNET) build $(PROJECT) -c $(CONFIG) -r $(RID)

run:
	$(DOTNET) run --project $(PROJECT) -c $(CONFIG) -r $(RID) -- --port 8340

release:
	$(DOTNET) build $(PROJECT) -c Release -r $(RID)

publish:
	$(DOTNET) publish $(PROJECT) -c Release -r $(RID) -o dist/$(RID)
	chmod +x dist/$(RID)/Jplus
	@echo "Built dist/$(RID)/Jplus"

package: publish
	rm -rf dist/jplus-$(RID)
	mkdir -p dist/jplus-$(RID)
	cp dist/$(RID)/Jplus README.md LICENSE dist/jplus-$(RID)/
	cp Resources/env.example dist/jplus-$(RID)/env.example
	tar -C dist -czf dist/jplus-$(RID).tar.gz jplus-$(RID)
	@echo "Built dist/jplus-$(RID).tar.gz"

# The binary is replaced by rename, so a running service keeps its old inode
# (no "Text file busy"); restart it afterwards: jplus stop && jplus start.
# data/, .env and the TLS files beside it are never touched.
install: publish
	mkdir -p "$(APPDIR)" "$(PREFIX)/bin"
	install -m 755 dist/$(RID)/Jplus "$(APPDIR)/Jplus.new"
	mv -f "$(APPDIR)/Jplus.new" "$(APPDIR)/Jplus"
	[ -e "$(APPDIR)/.env" ] || install -m 600 Resources/env.example "$(APPDIR)/.env"
	ln -sf "$(APPDIR)/Jplus" "$(PREFIX)/bin/jplus"
	@echo "Installed $(APPDIR)/Jplus (on PATH as jplus). Settings: $(APPDIR)/.env"

service: install
	"$(APPDIR)/Jplus" install
	"$(APPDIR)/Jplus" start

uninstall:
	-[ -x "$(APPDIR)/Jplus" ] && "$(APPDIR)/Jplus" uninstall
	rm -f "$(APPDIR)/Jplus" "$(PREFIX)/bin/jplus"
	@echo "Removed the binary. Data and settings are kept in $(APPDIR) - delete it by hand if unwanted."

clean:
	rm -rf dist
	-$(DOTNET) clean $(PROJECT) -c $(CONFIG)
	rm -rf bin obj
