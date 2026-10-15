"""Collaudo di riallinea-riferimenti-doc.py su un repository git temporaneo.

Il sorgente ha due revisioni: nella seconda tre righe entrano in testa e un metodo sparisce,
cosi' ogni ancora scritta sulla prima e' slittata. Il documento copre i casi che lo strumento
deve distinguere: prova forte, prova debole, intervallo, citazione di intestazione, riga
sparita, riga generica, ancora gia' con frammento, parentesi quadra nel frammento. Si
controlla anche che senza --scrivi non tocchi nulla e che il BOM del documento sopravviva.

Uso: python WCS/tools/tests/test_riallinea_riferimenti_doc.py
"""
import importlib.util
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

TOOL = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "riallinea-riferimenti-doc.py"))

SOURCE_V1 = """using System;

namespace Demo;

public static class Engine
{
    public static void Start()
    {
        Console.WriteLine("start");
    }

    public static void Legacy()
    {
        Console.WriteLine("legacy");
    }

    [Obsolete("use Compute2")]
    public static int Compute(int value)
    {
        return value * 2;
    }

    public static void Stop()
    {
        Console.WriteLine("stop");
        return;
    }
}
"""

# Tre righe in testa (+3) e Legacy tolto (-5 dalla riga 12 in poi).
SOURCE_V2 = SOURCE_V1.replace("using System;\n", "using System;\n// riga uno\n// riga due\n// riga tre\n", 1)
SOURCE_V2 = SOURCE_V2.replace(
    '    public static void Legacy()\n    {\n        Console.WriteLine("legacy");\n    }\n\n', "", 1)

DOC_V1 = """# Guida

Forte: [Engine.cs:7](src/Engine.cs:7) (`Start`).
Intervallo: [Engine.cs:18-21](src/Engine.cs:18) (`Compute`).
Debole: [:20](src/Engine.cs:20).
Tenuta senza prova: [Engine.cs:23](src/Engine.cs:23) (`Stop`).
Intestazione: [Engine.cs:1](src/Engine.cs:1).
Sparita: [Engine.cs:12](src/Engine.cs:12) (`Legacy`).
Quadra: [Engine.cs:17](src/Engine.cs:17) (`Obsolete`).
Gia' fatta: [Engine.cs:5 «class Engine»](src/Engine.cs:5).
Generica: [:26](src/Engine.cs:26).
"""

EXPECTED = {
    "Forte": "[Engine.cs:10 «public static void Start()»](src/Engine.cs:10) (`Start`).",
    "Intervallo": "[Engine.cs:16-19 «public static int Compute(int value)»](src/Engine.cs:16) (`Compute`).",
    "Debole": "[:18 «return value * 2;»](src/Engine.cs:18).",
    "Tenuta senza prova": "[Engine.cs:23 «Console.WriteLine(\"stop\");»](src/Engine.cs:23) (`Stop`).",
    "Intestazione": "[Engine.cs:1 «using System;»](src/Engine.cs:1).",
    "Sparita": "[Engine.cs:12](src/Engine.cs:12) (`Legacy`).",
    "Quadra": "[Engine.cs:15 «Obsolete(\"use Compute2\")»](src/Engine.cs:15) (`Obsolete`).",
    "Gia' fatta": "[Engine.cs:5 «class Engine»](src/Engine.cs:5).",
    "Generica": "[:26](src/Engine.cs:26).",
}


def git(root, *args):
    subprocess.run(["git", "-C", root, "-c", "user.name=test", "-c", "user.email=test@example.com",
                    "-c", "core.autocrlf=false", *args], check=True, capture_output=True)


def load_tool():
    spec = importlib.util.spec_from_file_location("riallinea", TOOL)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class RepositoryFixture(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="riallinea-")
        os.makedirs(os.path.join(self.root, "src"))
        os.makedirs(os.path.join(self.root, "doc"))
        self.source = os.path.join(self.root, "src", "Engine.cs")
        self.doc = os.path.join(self.root, "doc", "guida.md")
        git(self.root, "init", "-q")
        self.write(self.source, SOURCE_V1)
        self.write(self.doc, DOC_V1, bom=True)
        git(self.root, "add", "-A")
        git(self.root, "commit", "-q", "-m", "prima revisione")
        self.write(self.source, SOURCE_V2)
        git(self.root, "add", "-A")
        git(self.root, "commit", "-q", "-m", "seconda revisione")

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    @staticmethod
    def write(path, text, bom=False):
        data = text.encode("utf-8")
        with io.open(path, "wb") as handle:
            handle.write((b"\xef\xbb\xbf" if bom else b"") + data)

    def run_tool(self, *extra):
        result = subprocess.run([sys.executable, TOOL, "--radice", self.root, "--percorso", "doc", *extra],
                                capture_output=True, encoding="utf-8")
        self.assertEqual(0, result.returncode, result.stderr)
        return result.stdout

    def doc_lines(self):
        with io.open(self.doc, "rb") as handle:
            raw = handle.read()
        return raw, raw.decode("utf-8-sig").split("\n")

    def line_for(self, lines, prefix):
        for line in lines:
            if line.startswith(prefix + ":"):
                return line[len(prefix) + 2:]
        self.fail(f"riga '{prefix}' non trovata nel documento")


class DryRun(RepositoryFixture):
    def test_senza_scrivi_il_documento_resta_uguale(self):
        before, _ = self.doc_lines()
        output = self.run_tool()
        after, _ = self.doc_lines()
        self.assertEqual(before, after)
        self.assertIn("Nessun documento modificato", output)
        self.assertIn("Ancore senza frammento: 8", output)


class Rewrite(RepositoryFixture):
    def setUp(self):
        super().setUp()
        self.report_path = os.path.join(self.root, "rapporto.json")
        self.output = self.run_tool("--scrivi", "--rapporto", self.report_path)
        self.raw, self.lines = self.doc_lines()
        with io.open(self.report_path, encoding="utf-8") as handle:
            self.report = {r["label"]: r for r in json.load(handle)}

    def test_ogni_caso_produce_la_riga_attesa(self):
        for prefix, expected in EXPECTED.items():
            with self.subTest(caso=prefix):
                self.assertEqual(expected, self.line_for(self.lines, prefix))

    def test_esiti_nel_rapporto(self):
        outcomes = {label: r["outcome"] for label, r in self.report.items()}
        self.assertEqual("riallineata", outcomes["Engine.cs:7"])
        self.assertEqual("riallineata", outcomes["Engine.cs:18-21"])
        self.assertEqual("riallineata-debole", outcomes[":20"])
        self.assertEqual("tenuta", outcomes["Engine.cs:23"])
        self.assertEqual("tenuta-intestazione", outcomes["Engine.cs:1"])
        self.assertEqual("sparita", outcomes["Engine.cs:12"])
        self.assertEqual("riallineata", outcomes["Engine.cs:17"])
        self.assertEqual("irrisolta", outcomes[":26"])
        self.assertNotIn("Engine.cs:5", outcomes, "un'ancora gia' con frammento non va rielaborata")

    def test_bom_conservato_e_fine_riga_lf(self):
        self.assertTrue(self.raw.startswith(b"\xef\xbb\xbf"))
        self.assertNotIn(b"\r", self.raw)

    def test_seconda_corsa_non_cambia_nulla(self):
        self.run_tool("--scrivi")
        raw, _ = self.doc_lines()
        self.assertEqual(self.raw, raw)


DOC_FRAMMENTI = """# Frammenti

Spostata: [Engine.cs:5 «class Engine»](src/Engine.cs:5).
Pertinente: [Engine.cs:1 «using System;»](src/Engine.cs:1).
Ambigua: [Engine.cs:9 «// riga»](src/Engine.cs:9).
Sparita: [Engine.cs:12 «Console.WriteLine("legacy");»](src/Engine.cs:12).
Intervallo: [Engine.cs:7-10 «public static void Start()»](src/Engine.cs:7).
"""

ATTESI_FRAMMENTI = {
    # Tre righe in testa: la classe scende dalla 5 alla 8, e il numero cambia nel link e nell'etichetta.
    "Spostata": "[Engine.cs:8 «class Engine»](src/Engine.cs:8).",
    "Pertinente": "[Engine.cs:1 «using System;»](src/Engine.cs:1).",
    # «// riga» sta su tre righe: l'occorrenza piu' vicina sarebbe una scommessa, quindi non si tocca.
    "Ambigua": "[Engine.cs:9 «// riga»](src/Engine.cs:9).",
    # Il codice citato non c'e' piu': va guardata a mano, non riallineata d'ufficio.
    "Sparita": '[Engine.cs:12 «Console.WriteLine("legacy");»](src/Engine.cs:12).',
    "Intervallo": "[Engine.cs:10-13 «public static void Start()»](src/Engine.cs:10).",
}


class Frammenti(RepositoryFixture):
    """La modalita' --frammenti: il frammento e' la prova, e l'ambiguita' si dichiara."""

    def setUp(self):
        super().setUp()
        self.doc = os.path.join(self.root, "doc", "frammenti.md")
        self.write(self.doc, DOC_FRAMMENTI)

    def test_ogni_caso_produce_la_riga_attesa(self):
        self.run_tool("--frammenti", "--scrivi")
        _, lines = self.doc_lines()
        for prefix, expected in ATTESI_FRAMMENTI.items():
            with self.subTest(caso=prefix):
                self.assertEqual(expected, self.line_for(lines, prefix))

    def test_esiti_nel_rapporto(self):
        report = os.path.join(self.root, "rapporto.json")
        self.run_tool("--frammenti", "--rapporto", report)
        with io.open(report, encoding="utf-8") as handle:
            esiti = {r["doc_line"]: r["outcome"] for r in json.load(handle) if r["doc"].endswith("frammenti.md")}
        self.assertEqual({3: "riallineata", 4: "pertinente", 5: "ambigua",
                          6: "frammento sparito", 7: "riallineata"}, esiti)

    def test_senza_scrivi_il_documento_resta_uguale(self):
        before, _ = self.doc_lines()
        self.run_tool("--frammenti")
        after, _ = self.doc_lines()
        self.assertEqual(before, after)

    def test_seconda_corsa_non_cambia_nulla(self):
        self.run_tool("--frammenti", "--scrivi")
        first, _ = self.doc_lines()
        self.run_tool("--frammenti", "--scrivi")
        second, _ = self.doc_lines()
        self.assertEqual(first, second)


class Fragment(unittest.TestCase):
    def setUp(self):
        self.tool = load_tool()

    def test_righe_generiche_non_danno_frammento(self):
        for line in ("return;", "break;", "{", "}", "/// <summary>", "", "    default:"):
            with self.subTest(riga=line):
                self.assertIsNone(self.tool.make_fragment(line))

    def test_quadra_spaiata_esclusa(self):
        self.assertEqual('Obsolete("x")', self.tool.make_fragment('    [Obsolete("x")]'))
        self.assertEqual("var device = PathDevices.Instance", self.tool.make_fragment("var device = PathDevices.Instance[id];"))

    def test_taglio_su_un_confine_di_parola(self):
        long_line = "    await CommandToDo.SetStatus(pathMission.IdCommandToDo, Command.StatusTypes.Error, errorDescription);"
        fragment = self.tool.make_fragment(long_line)
        self.assertLessEqual(len(fragment), self.tool.FRAGMENT_WIDTH)
        self.assertIn(fragment, long_line)
        self.assertFalse(fragment.endswith((",", " ", "(")))

    def test_identificatori_forti_scartano_le_parole_comuni(self):
        strong = self.tool.strong_identifiers("chiama `Items.Add` e poi `RiaccodaConAttesaCrescente(mission)`")
        self.assertEqual({"RiaccodaConAttesaCrescente", "mission"}, strong)


if __name__ == "__main__":
    unittest.main(verbosity=2)
