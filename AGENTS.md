# LittleLoop agent guidance

Read README.md, docs/06-design-baseline.md, and task-relevant design documents. This project is currently a research/design proposal; do not describe planned cognitive capabilities as implemented.

The project construction model is `.singularity/model.json` (Singularity 0.4 / format 3). Read `.singularity/README.md` for the local tool path and usage.

- Use relevant model context plus actual files; the graph is intentionally incomplete.
- Only explicitly human-confirmed intent is authoritative; extracted design text remains candidate.
- Keep authorization, implementation and verification status distinct.
- Use shared apply transactions with expected revision when modifying the model.
- Derive routine engineering details, expose unresolved contradictions, and record evidence for claims.
- Feed concrete modeling difficulties back into Singularity architecture improvements rather than forcing all detail into prose or inventing confirmation.
