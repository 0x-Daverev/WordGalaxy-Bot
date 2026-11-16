#!/usr/bin/env python3
"""Tests of tools/protocol_catalog.py with a tiny fake client and bot.

    python3 -I tools/test_protocol_catalog.py
"""
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import protocol_catalog  # noqa: E402

CMD = """package §_-G4V§
{
   public class {cls}{extends} implements ICommand
   {
      public static const ID:int = {id};
      public var §_-a§:int = 0;
      public var name:String = "";
      public function read(param1:IDataInput) : void {{ param1.readInt(); param1.readUTF(); }}
   }}
}}
"""


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)


class CatalogTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = self.tmp.name
        self.client = os.path.join(root, "scripts")
        g = os.path.join(self.client, "§_-G4V§")
        write(os.path.join(g, "§_-A§.as"), CMD.replace("{cls}", "§_-A§").replace("{extends}", "").replace("{id}", "100"))
        write(os.path.join(g, "§_-B§.as"), CMD.replace("{cls}", "§_-B§").replace("{extends}", " extends §_-Base§").replace("{id}", "200"))
        write(os.path.join(g, "§_-C§.as"), CMD.replace("{cls}", "§_-C§").replace("{extends}", "").replace("{id}", "300"))
        # Dispatch table: the server sends A and B; C is sent by the client.
        write(os.path.join(self.client, "§_-n1i§", "§_-T§.as"),
              "package §_-n1i§ {\n import §_-G4V§.*;\n import net.game.ShipHandler;\n"
              " t[§_-A§.ID] = §_-HA§;\n t[§_-B§.ID] = §_-HB§;\n}\n")
        write(os.path.join(self.client, "§_-h§", "§_-HA§.as"),
              'package §_-h§ {\n import net.game.mvc.UpdateShipCommand;\n public class §_-HA§ { var s = "msg_ship_ready"; }\n}\n')
        self.repo = os.path.join(root, "bot")
        core = os.path.join(self.repo, "PacketBot.Core")
        write(os.path.join(core, "Commands", "Read", "A.cs"),
              "namespace X { public class ReadA : Base { public const short ID = 100; } }")
        write(os.path.join(core, "Commands", "Write", "C.cs"),
              "namespace X { public class WriteC { public const short ID = 300; } }")
        write(os.path.join(core, "PacketHandlers", "PacketManager.cs"),
              'switch (id) { case ReadA.ID: break; case 4224: var m = "0|URI|"; break; }')
        self.telemetry = os.path.join(root, "telemetry")
        write(os.path.join(self.telemetry, "2026-11-15-1.json"), json.dumps({
            "Packets": {"200": {"Count": 7, "MinBytes": 4, "MaxBytes": 9, "Maps": {"307": 7}, "Samples": ["00"]},
                        "999": {"Count": 1, "MinBytes": 1, "MaxBytes": 1, "Maps": {}, "Samples": []}},
            "TextKeys": {"URI": 3, "n|pvp": 2}}))
        self.out = os.path.join(root, "out")

    def tearDown(self):
        self.tmp.cleanup()

    def test_catalog_and_coverage(self):
        code = protocol_catalog.main(["--client", self.client, "--repo", self.repo,
                                      "--telemetry", self.telemetry, "--out", self.out])
        self.assertEqual(0, code)
        catalog = {c["id"]: c for c in json.loads(protocol_catalog.read_text(os.path.join(self.out, "catalog.json")))}
        self.assertEqual({100, 200, 300}, set(catalog))
        self.assertTrue(catalog[200]["incoming"])  # "extends ... implements ICommand"
        self.assertFalse(catalog[300]["incoming"])
        self.assertEqual("ReadA", catalog[100]["botReads"])
        self.assertEqual("WriteC", catalog[300]["botWrites"])
        self.assertIn("UpdateShipCommand", catalog[100]["handler"]["imports"])
        self.assertIn("msg_ship_ready", catalog[100]["handler"]["strings"])
        self.assertEqual(7, catalog[200]["seen"]["count"])
        report = protocol_catalog.read_text(os.path.join(self.out, "COVERAGE.md"))
        self.assertIn("| 200 | 7 | 4-9 | 307 |", report)  # pending, with telemetry
        self.assertIn("| `URI` | 3 | sí |", report)
        self.assertIn("| `n|pvp` | 2 | no |", report)
        self.assertIn("999", report)  # seen but not in the client


if __name__ == "__main__":
    unittest.main()
