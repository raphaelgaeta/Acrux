# Copr builds Acrux's RPM with its "make srpm" method: it clones this
# repository at the committish given and runs, as root in a Fedora chroot with
# network,
#
#   make -f .copr/Makefile srpm outdir=<dir> spec=<spec>
#
# The RPM repackages the release's own Linux packages (installer/rpm/acrux.spec),
# so this downloads them, checks them against the release's SHA256SUMS and
# builds the source RPM. Which release:
# - VERSION=X.Y.Z, when given;
# - the tag, when the committish is a release tag (v0.3.1);
# - otherwise the latest published release on GitHub (drafts don't count),
#   so Copr can build from master with nothing to edit at each release.
# To try it locally:
#
#   make -f .copr/Makefile srpm outdir=/tmp/srpm VERSION=0.3.1

top := $(abspath $(dir $(lastword $(MAKEFILE_LIST)))..)
specfile := $(top)/installer/rpm/acrux.spec
outdir ?= $(CURDIR)
VERSION ?=

srpm:
	command -v git > /dev/null && command -v rpmbuild > /dev/null && command -v spectool > /dev/null \
		|| dnf -y install git-core rpm-build rpmdevtools
	set -eu; \
	version='$(VERSION)'; \
	if [ -z "$$version" ]; then \
		tag=$$(git -c safe.directory='*' -C '$(top)' describe --tags --exact-match 2>/dev/null || true); \
		case "$$tag" in v[0-9]*) version=$${tag#v};; esac; \
	fi; \
	if [ -z "$$version" ]; then \
		latest=$$(curl -fsSLI -o /dev/null -w '%{url_effective}' https://github.com/raphaelgaeta/Acrux/releases/latest); \
		version=$${latest##*/tag/v}; \
	fi; \
	echo "$$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$$' \
		|| { echo "no release version (got '$$version'): pass VERSION=X.Y.Z" >&2; exit 1; }; \
	echo "building the source RPM of Acrux $$version"; \
	src=$$(mktemp -d); \
	spectool -g -C "$$src" --define "acrux_version $$version" '$(specfile)'; \
	curl -fsSL -o "$$src/SHA256SUMS" "https://github.com/raphaelgaeta/Acrux/releases/download/v$$version/SHA256SUMS"; \
	files="acrux-cli-v$$version-linux-x64.tar.gz acrux-cli-v$$version-linux-arm64.tar.gz Acrux-v$$version-linux-x64.tar.gz"; \
	for f in $$files; do \
		grep -q " $$f$$" "$$src/SHA256SUMS" || { echo "$$f is not in the release's SHA256SUMS" >&2; exit 1; }; \
	done; \
	(cd "$$src" && for f in $$files; do grep " $$f$$" SHA256SUMS; done | sha256sum -c -); \
	mkdir -p '$(outdir)'; \
	rpmbuild -bs --define "_sourcedir $$src" --define "_srcrpmdir $(outdir)" \
		--define "acrux_version $$version" '$(specfile)'; \
	rm -rf "$$src"

.PHONY: srpm
