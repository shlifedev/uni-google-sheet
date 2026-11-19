import { useState } from "react";
import { useApp } from "../store";
import { Count, T } from "../i18n";
import { copy, openProblem } from "../lib/actions";
import { showMenu } from "./Menu";
import { Chevron, ErrorIcon, WarningIcon } from "./Icons";
import type { Diagnostic } from "../types";

/** The one-line bar under the data: error and warning counts; clicking it opens or closes the list. */
export function ProblemsBar() {
  const { run, problemsOpen, setProblemsOpen } = useApp();
  const problems = run?.diagnostics ?? [];
  const errors = problems.filter((problem) => problem.severity === "error").length;
  const warnings = problems.length - errors;
  return (
    <div className="problems-bar" onClick={() => setProblemsOpen(!problemsOpen)}>
      <span className="twisty" style={{ display: "inline-flex", transform: problemsOpen ? "rotate(90deg)" : undefined, color: "var(--text-3)" }}>
        <Chevron />
      </span>
      <strong>{T("Problems")}</strong>
      <span className={`badge ${errors > 0 ? "error" : ""}`}>{Count(errors, "error")}</span>
      <span className={`badge ${warnings > 0 ? "warning" : ""}`}>{Count(warnings, "warning")}</span>
      <div className="fill" />
      {problems.length > 0 && (
        <button
          className="button small ghost"
          onClick={(event) => {
            event.stopPropagation();
            void copy(problems.map((problem) => problem.text).join("\n"));
          }}
        >
          {T("Copy all")}
        </button>
      )}
    </div>
  );
}

/** Every problem of the last run, errors first. Double-click opens the file at the problem. */
export function ProblemsList() {
  const { run } = useApp();
  const [selected, setSelected] = useState<number>(-1);
  const problems = run?.diagnostics ?? [];

  const menu = (event: React.MouseEvent, problem: Diagnostic) =>
    showMenu(event, [
      { label: T("Open file"), action: () => void openProblem(problem) },
      { separator: true },
      { label: T("Copy"), action: () => void copy(problem.text) },
      { label: T("Copy message"), action: () => void copy(problem.message) },
      { label: T("Copy location"), action: () => void copy(problem.location) },
      ...(problem.hint ? [{ label: T("Copy fix"), action: () => void copy(problem.hint) }] : []),
    ]);

  if (problems.length === 0) {
    return <div className="empty">{run ? T("No problems.") : T("Run a check to see problems.")}</div>;
  }

  return (
    <div className="problem-list">
      {problems.map((problem, index) => (
        <div
          key={index}
          className={`problem ${selected === index ? "selected" : ""}`}
          onMouseDown={() => setSelected(index)}
          onDoubleClick={() => void openProblem(problem)}
          onContextMenu={(event) => {
            setSelected(index);
            menu(event, problem);
          }}
        >
          <span className={`icon ${problem.severity}`}>{problem.severity === "error" ? <ErrorIcon /> : <WarningIcon />}</span>
          <div className="fill">
            <div className="selectable">{problem.message}</div>
            <div className="detail">
              <span>{problem.location}</span>
              {problem.hint && <span className="hint">{problem.hint}</span>}
            </div>
          </div>
        </div>
      ))}
    </div>
  );
}
