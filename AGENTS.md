# LittleLoop agent guidance

Read README.md, docs/06-design-baseline.md, and task-relevant design documents. The phase 1 simulated ticket loop is implemented. Distinguish it from the capabilities planned in later phases.

The project construction model is `.singularity/model.json` (Singularity 0.4 / format 3). Use the pinned repository-local CLI `python tools/model.py`; read `.singularity/README.md` for setup and usage. Do not depend on an adjacent Singularity checkout.

- Find relevant nodes with `python tools/model.py find <query>`, then use bounded `context --summary --max-nodes 12 --max-bytes 8192` and `get <ID>`. Do not routinely read the entire model or use `list` as the discovery command. The graph and bounded results are incomplete; inspect omitted dependencies before decisions.
- Only explicitly human-confirmed intent is authoritative; extracted design text remains candidate.
- Keep authorization, implementation and verification status distinct.
- Modify the model only through `python tools/model.py apply` transactions with expected revision, dry-run and readback. Do not use direct JSON editing as the normal workflow. A passing validator does not prove who wrote a change or how.
- Derive routine engineering details, expose unresolved contradictions, and record evidence for claims.
- Feed concrete modeling difficulties back into Singularity architecture improvements rather than forcing all detail into prose or inventing confirmation.
