#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
[AEGIS-MVP-GOOGLE-SECOPS-02] Gerador/verificador DETERMINÍSTICO do catálogo compacto MITRE ATT&CK.

Deriva o artefato de referência `backend/src/AegisScore.Api/Data/mitre_attack_enterprise_v17_1.json`
EXCLUSIVAMENTE do STIX OFICIAL versionado do MITRE (attack-stix-data), nunca de blog, tabela autoral
ou IA. A versão é FIXADA em Enterprise ATT&CK v17.1 (alinhada à v17 suportada pelo Google SecOps), com a
versão, a URL de origem, o SHA-256 da fonte e a atribuição/licença registrados na proveniência do artefato.

O runtime e os testes do AEGIS NÃO dependem de internet: eles leem apenas o JSON compacto commitado. Só
este script (opt-in) baixa o STIX oficial; a verificação em CI recomputa o catálogo a partir de um STIX
local e o compara ao artefato commitado (ignorando o timestamp de geração).

Uso:
  # Gerar a partir de um STIX local:
  python scripts/mitre/generate_mitre_catalog.py --stix <enterprise-attack-17.1.json> --write

  # Baixar o STIX oficial v17.1 e gerar:
  python scripts/mitre/generate_mitre_catalog.py --download --write

  # Verificar o artefato commitado contra o STIX (falha se divergir):
  python scripts/mitre/generate_mitre_catalog.py --stix <enterprise-attack-17.1.json> --verify

Sem --write nem --verify, apenas imprime um resumo.
"""
import argparse
import hashlib
import json
import os
import re
import sys
import urllib.request

ATTACK_VERSION = "17.1"
DOMAIN = "enterprise-attack"
SOURCE_PAGE = (
    "https://github.com/mitre-attack/attack-stix-data/blob/master/"
    "enterprise-attack/enterprise-attack-17.1.json"
)
SOURCE_RAW = (
    "https://raw.githubusercontent.com/mitre-attack/attack-stix-data/master/"
    "enterprise-attack/enterprise-attack-17.1.json"
)
# Termos de uso do ATT&CK (Apache-2.0 para o conteúdo STIX). Atribuição obrigatória.
LICENSE = "Apache-2.0"
ATTRIBUTION = (
    "MITRE ATT&CK® — © The MITRE Corporation. Reproduzido do conjunto STIX oficial "
    "(mitre-attack/attack-stix-data) sob os Termos de Uso do ATT&CK. "
    "https://attack.mitre.org/resources/legal-and-branding/terms-of-use/"
)

# Formatos MITRE documentados e validáveis (os únicos aceitos).
TECHNIQUE_RE = re.compile(r"^T\d{4}(?:\.\d{3})?$")
TACTIC_RE = re.compile(r"^TA\d{4}$")

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DEFAULT_OUT = os.path.join(
    REPO_ROOT, "backend", "src", "AegisScore.Api", "Data",
    "mitre_attack_enterprise_v17_1.json",
)


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def download_stix(dest: str) -> str:
    print(f"Baixando STIX oficial v{ATTACK_VERSION} de {SOURCE_RAW} ...", file=sys.stderr)
    with urllib.request.urlopen(SOURCE_RAW, timeout=180) as resp:  # noqa: S310 (URL fixa oficial)
        data = resp.read()
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with open(dest, "wb") as fh:
        fh.write(data)
    return dest


def external_attack_id(obj) -> str | None:
    for ref in obj.get("external_references", []):
        if ref.get("source_name") == "mitre-attack" and ref.get("external_id"):
            return ref["external_id"]
    return None


def build_catalog(stix_bytes: bytes):
    """Deriva (tactics, techniques, source_sha256) do bundle STIX oficial. Determinístico e ordenado."""
    source_sha = sha256_hex(stix_bytes)
    bundle = json.loads(stix_bytes.decode("utf-8"))
    objects = bundle.get("objects", [])

    collection = next((o for o in objects if o.get("type") == "x-mitre-collection"), None)
    version = collection.get("x_mitre_version") if collection else None
    spec = collection.get("x_mitre_attack_spec_version") if collection else None
    if version != ATTACK_VERSION:
        raise SystemExit(
            f"STIX não é Enterprise ATT&CK v{ATTACK_VERSION} (x_mitre_version={version!r}). "
            "Este pacote fixa a v17.1 deliberadamente — não use silenciosamente outra versão."
        )

    # Táticas: shortname -> (TA id, nome).
    tactic_by_short = {}
    tactics = []
    for o in objects:
        if o.get("type") != "x-mitre-tactic":
            continue
        ta_id = external_attack_id(o)
        short = o.get("x_mitre_shortname")
        name = o.get("name")
        if not (ta_id and short and name and TACTIC_RE.match(ta_id)):
            continue
        tactic_by_short[short] = ta_id
        tactics.append({"id": ta_id, "shortName": short, "name": name})
    tactics.sort(key=lambda t: t["id"])

    techniques = []
    for o in objects:
        if o.get("type") != "attack-pattern":
            continue
        tid = external_attack_id(o)
        name = o.get("name")
        if not (tid and name and TECHNIQUE_RE.match(tid)):
            continue
        is_sub = bool(o.get("x_mitre_is_subtechnique"))
        parent = tid.split(".")[0] if (is_sub and "." in tid) else None
        # Táticas relacionadas: kill_chain_phases da matriz ATT&CK -> TA id (autoridade = catálogo).
        tac_ids = []
        for ph in o.get("kill_chain_phases", []):
            if ph.get("kill_chain_name") != "mitre-attack":
                continue
            ta = tactic_by_short.get(ph.get("phase_name"))
            if ta and ta not in tac_ids:
                tac_ids.append(ta)
        tac_ids.sort()
        techniques.append({
            "id": tid,
            "name": name,
            "isSubtechnique": is_sub,
            "parent": parent,
            "tactics": tac_ids,
            "revoked": bool(o.get("revoked")),
            "deprecated": bool(o.get("x_mitre_deprecated")),
        })
    # Ordenação estável e determinística por ID.
    techniques.sort(key=lambda t: t["id"])

    return tactics, techniques, source_sha, spec


def content_hash(tactics, techniques) -> str:
    """Hash SHA-256 do CONTEÚDO (táticas + técnicas), independente da proveniência/timestamp."""
    canonical = json.dumps(
        {"tactics": tactics, "techniques": techniques},
        ensure_ascii=False, sort_keys=True, separators=(",", ":"),
    )
    return sha256_hex(canonical.encode("utf-8"))


def assemble(tactics, techniques, source_sha, spec) -> dict:
    return {
        "provenance": {
            "dataset": "MITRE ATT&CK Enterprise",
            "attackVersion": ATTACK_VERSION,
            "attackSpecVersion": spec,
            "domain": DOMAIN,
            "alignmentNote": (
                "MITRE ATT&CK Enterprise v17.1 — alinhado à versão 17 suportada pelo Google SecOps. "
                "NÃO é apresentado como a versão global mais recente do MITRE."
            ),
            "source": SOURCE_PAGE,
            "sourceRaw": SOURCE_RAW,
            "sourceSha256": source_sha,
            "contentSha256": content_hash(tactics, techniques),
            "license": LICENSE,
            "attribution": ATTRIBUTION,
            "generator": "scripts/mitre/generate_mitre_catalog.py",
        },
        "tactics": tactics,
        "techniques": techniques,
    }


def write_catalog(doc: dict, out_path: str):
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(doc, fh, ensure_ascii=False, indent=2, sort_keys=False)
        fh.write("\n")


def main():
    ap = argparse.ArgumentParser(description="Gera/verifica o catálogo compacto MITRE ATT&CK v17.1.")
    ap.add_argument("--stix", help="Caminho do STIX enterprise-attack-17.1.json local.")
    ap.add_argument("--download", action="store_true", help="Baixa o STIX oficial v17.1 antes de gerar.")
    ap.add_argument("--out", default=DEFAULT_OUT, help="Caminho do artefato de saída.")
    ap.add_argument("--write", action="store_true", help="Escreve o artefato em --out.")
    ap.add_argument("--verify", action="store_true",
                    help="Verifica o artefato em --out contra o STIX (falha se divergir).")
    args = ap.parse_args()

    stix_path = args.stix
    if args.download:
        stix_path = stix_path or os.path.join(REPO_ROOT, "artifacts", "enterprise-attack-17.1.json")
        download_stix(stix_path)
    if not stix_path or not os.path.exists(stix_path):
        raise SystemExit("Informe --stix <arquivo> (ou --download). O STIX oficial v17.1 é obrigatório.")

    with open(stix_path, "rb") as fh:
        stix_bytes = fh.read()

    tactics, techniques, source_sha, spec = build_catalog(stix_bytes)
    doc = assemble(tactics, techniques, source_sha, spec)

    active = [t for t in techniques if not t["revoked"] and not t["deprecated"]]
    print(
        f"ATT&CK v{ATTACK_VERSION} (spec {spec}) — táticas: {len(tactics)}, "
        f"técnicas: {len(techniques)} (ativas: {len(active)}, "
        f"sub: {sum(1 for t in techniques if t['isSubtechnique'])}); "
        f"sourceSha256={source_sha[:12]}… contentSha256={doc['provenance']['contentSha256'][:12]}…",
        file=sys.stderr,
    )

    if args.verify:
        if not os.path.exists(args.out):
            raise SystemExit(f"Artefato não encontrado para verificação: {args.out}")
        with open(args.out, "r", encoding="utf-8") as fh:
            committed = json.load(fh)
        cur = content_hash(committed.get("tactics", []), committed.get("techniques", []))
        expected = doc["provenance"]["contentSha256"]
        prov = committed.get("provenance", {})
        problems = []
        if cur != expected:
            problems.append(f"contentSha256 divergente (commitado gera {cur}, STIX gera {expected}).")
        if prov.get("contentSha256") != cur:
            problems.append(
                f"contentSha256 da proveniência ({prov.get('contentSha256')}) não bate com o conteúdo ({cur}).")
        if prov.get("attackVersion") != ATTACK_VERSION:
            problems.append(f"attackVersion commitada = {prov.get('attackVersion')!r}, esperado {ATTACK_VERSION!r}.")
        if prov.get("sourceSha256") != source_sha:
            problems.append("sourceSha256 commitado não bate com o STIX fornecido.")
        if problems:
            for p in problems:
                print("VERIFY FALHOU:", p, file=sys.stderr)
            raise SystemExit(1)
        print("VERIFY OK — artefato commitado é idêntico ao derivado do STIX oficial.", file=sys.stderr)
        return

    if args.write:
        write_catalog(doc, args.out)
        print(f"Escrito: {args.out}", file=sys.stderr)


if __name__ == "__main__":
    main()
