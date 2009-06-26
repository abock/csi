ASSEMBLY = csi.exe
REFERENCES = -r:./Mono.CSharp.dll -r:Mono.Management
SOURCES = \
	repl.cs \
	getline.cs \
	options.cs \
	DataConverter.cs

PREFIX :=
DATADIR := $(PREFIX)/share
BINDIR := $(PREFIX)/bin
PKGDATADIR := $(DATADIR)/csi

$(ASSEMBLY): $(SOURCES)
	gmcs -out:$@ -debug -unsafe $(REFERENCES) $(SOURCES)

configure:
	@if [ -z "$(PREFIX)" ]; then \
		echo "You must set PREFIX before installing/uninstalling:"; \
		echo; \
		echo "   $$ sudo make install PREFIX=/usr"; \
		echo; \
		exit 1; \
	fi;

csi: csi.in
	sed 's,@PKGDATADIR@,$(PKGDATADIR),g' < $< > $@

install: configure csi $(ASSEMBLY)
	mkdir -p $(BINDIR)
	install -m 0755 csi $(BINDIR)
	mkdir -p $(PKGDATADIR)
	install -m 0644 $(ASSEMBLY) $(PKGDATADIR)
	install -m 0644 $(ASSEMBLY).mdb $(PKGDATADIR)
	install -m 0644 Mono.CSharp.dll $(PKGDATADIR)

uninstall: configure
	rm -f $(BINDIR)/csi
	rm -rf $(PKGDATADIR)

clean:
	rm -f $(ASSEMBLY){,.mdb} csi

