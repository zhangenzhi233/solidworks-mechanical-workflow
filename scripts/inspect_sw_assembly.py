"""Audit resolved paths in one SOLIDWORKS assembly configuration."""
import argparse
from collections import Counter
import json
from pathlib import Path
import re
import sys


def call(obj, name, *args):
    # Some zero-argument COM methods look like properties to late-bound pywin32.
    # Mark known methods explicitly so a returned document is never called as a function.
    obj._FlagAsMethod(name)
    return getattr(obj, name)(*args)


def matching_documents(assembly, pid, major):
    import pythoncom
    from win32com.client import Dispatch
    rot = pythoncom.GetRunningObjectTable()
    ctx = pythoncom.CreateBindCtx(0)
    matches = []
    for moniker in rot.EnumRunning():
        try:
            label = moniker.GetDisplayName(ctx, None)
        except pythoncom.com_error:
            continue
        match = re.fullmatch(r"!?SolidWorks_PID_(\d+)", label, re.IGNORECASE)
        if not match or (pid is not None and int(match[1]) != pid):
            continue
        app = Dispatch(rot.GetObject(moniker).QueryInterface(pythoncom.IID_IDispatch))
        if major is not None and call(app, "RevisionNumber").split(".")[0] != str(major):
            continue
        doc = call(app, "GetOpenDocumentByName", str(assembly))
        if doc is None:
            continue
        loaded = call(doc, "GetPathName")
        if loaded and Path(loaded).resolve() == assembly:
            matches.append((int(match[1]), app, doc))
    if len(matches) > 1:
        raise ValueError(f"File open in multiple SOLIDWORKS instances. Choose --pid from {[m[0] for m in matches]}")
    return matches[0] if matches else None


def component_row(component, root):
    row = {"instance": component.Name2, "configuration": component.ReferencedConfiguration}
    state = call(component, "GetSuppression2")
    row["suppression_code"] = state
    if state == 0:
        return {**row, "status": "suppressed"}
    row["component_path"] = call(component, "GetPathName") or ""
    if state not in (2, 3):
        return {**row, "status": "unresolved"}
    if component.IsVirtual:
        return {**row, "status": "virtual_review"}
    doc = call(component, "GetModelDoc2")
    loaded = call(doc, "GetPathName") if doc is not None else None
    if not loaded or not Path(loaded).is_absolute():
        return {**row, "status": "unresolved"}
    actual = Path(loaded).resolve()
    row["loaded_path"] = str(actual)
    if not actual.is_file():
        return {**row, "status": "missing_on_disk"}
    cached = row["component_path"]
    row["path_differs"] = not cached or Path(cached).resolve() != actual
    return {**row, "status": "inside" if actual.is_relative_to(root) else "external"}


def inspect_document(app, doc, assembly, root):
    actual = call(doc, "GetPathName")
    if not actual or Path(actual).resolve() != assembly or not assembly.is_relative_to(root):
        raise ValueError("The active document path must match the requested assembly inside the package root")
    config = doc.ConfigurationManager.ActiveConfiguration.Name
    rows = []
    for component in call(doc, "GetComponents", False) or ():
        try:
            rows.append(component_row(component, root))
        except Exception as exc:
            rows.append({"instance": str(component.Name2), "status": "unreadable", "error": str(exc)})
    counts = Counter(r["status"] for r in rows)
    incomplete = (not (counts["inside"] or counts["external"]) or
                  any(s not in ("inside", "external", "suppressed") for s in counts))
    if doc.ConfigurationManager.ActiveConfiguration.Name != config:
        raise RuntimeError("The active configuration changed during the inspection. Repeat the read.")
    if incomplete:
        status, code = "incomplete", 2
    elif counts["external"]:
        status, code = "external", 1
    else:
        status, code = "pass", 0
    return {"status": status, "assembly": str(assembly), "root": str(root), "pid": call(app, "GetProcessID"),
            "revision": call(app, "RevisionNumber"), "configuration": config,
            "counts": dict(counts), "instances": rows, "opened_by_tool": False,
            "open_warning_code": None}, code


def audit(assembly, root, pid, open_readonly, major):
    if assembly.suffix.lower() != ".sldasm" or not assembly.is_file():
        raise ValueError("Provide an existing .SLDASM file")
    if not root.is_dir() or not assembly.is_relative_to(root):
        raise ValueError("The expected package root must contain the input assembly")
    if major is not None and major < 1:
        raise ValueError("The SOLIDWORKS API major revision must be a positive integer")
    found = matching_documents(assembly, pid, major)
    if found:
        _, app, doc = found
        return inspect_document(app, doc, assembly, root)
    if not open_readonly or pid is not None:
        raise ValueError("This exact open document is unavailable in the running object table. "
                         "Use --open-readonly on the verification copy to access it by path.")

    import pythoncom
    from win32com.client import DispatchEx, VARIANT
    prog_id = "SldWorks.Application" if major is None else f"SldWorks.Application.{major}"
    app = DispatchEx(prog_id)
    if major is not None and call(app, "RevisionNumber").split(".")[0] != str(major):
        raise ValueError(f"The selected SOLIDWORKS instance does not report major revision {major}")
    prior = app.ActiveDoc
    previous_title = call(prior, "GetTitle") if prior is not None else None
    doc = call(app, "GetOpenDocumentByName", str(assembly))
    opened_here = doc is None
    error = VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)
    warning = VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)
    if opened_here:
        doc = call(app, "OpenDoc6", str(assembly), 2, 1 | 2 | 64, "", error, warning)
    actual = call(doc, "GetPathName") if doc is not None else ""
    if not actual or Path(actual).resolve() != assembly:
        raise ValueError(f"Could not open the exact document. SOLIDWORKS code {error.value}, actual path {actual}")
    try:
        if error.value:
            raise ValueError(f"SOLIDWORKS open error {error.value}")
        report, code = inspect_document(app, doc, assembly, root)
        if opened_here:
            report.update(opened_by_tool=True, open_warning_code=warning.value)
            reference_flags = {1: "document_id_mismatch", 32768: "missing_referenced_configuration",
                               1048576: "missing_external_references"}
            report["reference_warnings"] = [name for bit, name in reference_flags.items() if warning.value & bit]
            if report["reference_warnings"]:
                report["path_status"] = report["status"]
                report["status"], code = "incomplete", 2
        return report, code
    finally:
        if opened_here:
            call(app, "CloseDoc", call(doc, "GetTitle"))
            if previous_title:
                call(app, "ActivateDoc3", previous_title, False, 0, error)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("assembly", type=Path)
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--pid", type=int)
    parser.add_argument("--major", type=int, help="Select a SOLIDWORKS API major revision (32 for release 2024).")
    parser.add_argument("--open-readonly", action="store_true",
                        help="Allow a desktop COM session to open the verification copy read-only.")
    args = parser.parse_args()
    try:
        report, code = audit(args.assembly.resolve(), args.root.resolve(), args.pid, args.open_readonly, args.major)
    except Exception as exc:
        report, code = {"status": "incomplete", "error": f"{type(exc).__name__}: {exc}"}, 2
    print(json.dumps(report, ensure_ascii=not sys.stdout.isatty(), indent=2))
    return code


if __name__ == "__main__":
    sys.exit(main())
