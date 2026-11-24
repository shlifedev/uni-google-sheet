import { useRef } from "react";

interface Props {
  direction: "horizontal" | "vertical";
  size: number;
  min: number;
  max: number;
  onSize: (size: number) => void;
  first: React.ReactNode;
  second: React.ReactNode;
  collapsed?: boolean;
}

export function Split({ direction, size, min, max, onSize, first, second, collapsed }: Props) {
  const container = useRef<HTMLDivElement>(null);
  const horizontal = direction === "horizontal";

  const startDrag = (event: React.PointerEvent) => {
    event.preventDefault();
    const box = container.current!.getBoundingClientRect();
    const move = (moved: PointerEvent) => {
      const next = horizontal ? moved.clientX - box.left : box.bottom - moved.clientY;
      onSize(Math.round(Math.max(min, Math.min(max, next))));
    };
    const up = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
      document.body.style.cursor = "";
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
    document.body.style.cursor = horizontal ? "col-resize" : "row-resize";
  };

  const handle = (
    <div
      className="split-handle"
      style={horizontal ? { width: 1, cursor: "col-resize", position: "relative" } : { height: 1, cursor: "row-resize", position: "relative" }}
      onPointerDown={startDrag}
    >
      <div
        style={
          horizontal
            ? { position: "absolute", inset: "0 -3px", cursor: "col-resize" }
            : { position: "absolute", inset: "-3px 0", cursor: "row-resize" }
        }
      />
    </div>
  );

  return (
    <div ref={container} style={{ display: "flex", flexDirection: horizontal ? "row" : "column", height: "100%", minHeight: 0, minWidth: 0 }}>
      {horizontal ? (
        <>
          <div style={{ width: size, flex: "none", minHeight: 0 }}>{first}</div>
          {handle}
          <div className="fill">{second}</div>
        </>
      ) : (
        <>
          <div className="fill">{first}</div>
          {!collapsed && handle}
          <div style={{ height: collapsed ? "auto" : size, flex: "none", minHeight: 0 }}>{second}</div>
        </>
      )}
    </div>
  );
}
