# Third-party notices

## ECDICT

- Upstream: https://github.com/skywind3000/ECDICT
- Data source: https://raw.githubusercontent.com/skywind3000/ECDICT/master/ecdict.csv
- License: MIT, Copyright (c) 2025 Linwei
- The full upstream license is included as `Dictionary/ECDICT-LICENSE.txt` in the portable package.
- Entry count and the source CSV SHA-256 are recorded in `Dictionary/source.json`.
- Conversion changes the storage format only; the definitions and morphology metadata are retained.

## English proofreading

- LanguageTool 6.6: https://github.com/languagetool-org/languagetool/tree/v6.6 ; LGPL 2.1 or later.
- Official Maven artifacts: https://repo.maven.apache.org/maven2/org/languagetool/ . English language rules and their runtime dependencies are bundled without modifying the original JARs. Their embedded license notices are retained. Dependency coordinates are recorded in `scripts/proofreader-pom.xml` and SHA-256 hashes in the embedded `source.json`.
- Eclipse Temurin OpenJDK 21 JRE for Windows x64: https://adoptium.net/ . GPL v2 with the Classpath Exception; the original `legal` directory and notices are retained in the bundled runtime. Download URL and upstream checksum are recorded in `source.json`.
- `EnglishProofreader.zip` is extracted under the program's `Proofreader` directory on first use. Original JAR files can be replaced there for debugging or exercising rights under the LGPL; the application reads their classes through the Java classpath. Free mode performs proofreading locally. Optional DeepSeek mode sends translation/proofreading text to the official DeepSeek API only when selected and configured by the user.
- `scripts/HoverLexProofreader.java` is this project's pipe-based worker, included with its compiled class in the component. Eclipse JDT ECJ 3.40.0 is used only at build time to compile it and is not included in the application runtime. Build compiler source: https://repo.maven.apache.org/maven2/org/eclipse/jdt/ecj/3.40.0/ ; its upstream checksum is verified before compilation.

## Inspiration

The application is independently implemented for Windows, inspired by the hover lookup workflow in https://github.com/xiaolai/XiaolaiDict. No Swift source, artwork, or licensed dictionaries from that project are included in the release or source archive.

## Windows components

UI Automation, WinForms, Windows user data protection and speech synthesis are provided by the user's Windows/.NET Framework installation. Their binaries are not redistributed by this project.
