// A small popup menu: right-click menus and the View menu. One open at a time; Esc or a click outside closes it.
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { create } from "zustand";

export type MenuItem =
  | { label: string; action: () => void; disabled?: boolean; checked?: boolean; hint?: string }
  | { separator: true }
  | { heading: string };

interface MenuState {
  open: { x: number; y: number; items: MenuItem[] } | null;
  show: (x: number, y: number, items: MenuItem[]) => void;
  close: () => void;
}

export const useMenu = create<MenuState>((set) => ({
  open: null,
  show: (x, y, items) => set({ open: { x, y, items } }),
  close: () => set({ open: null }),
}));

/** Shows a menu at the mouse. For onContextMenu handlers. */
export function showMenu(event: React.MouseEvent, items: MenuItem[]) {
  event.preventDefault();
  event.stopPropagation();
  useMenu.getState().show(event.clientX, event.clientY, items);
}

/** Shows a menu under an element. For buttons. */
export function showMenuBelow(element: HTMLElement, items: MenuItem[]) {
  const box = element.getBoundingClientRect();
  useMenu.getState().show(box.left, box.bottom + 4, items);
}

export function MenuHost() {
  const { open, close } = useMenu();
  const ref = useRef<HTMLDivElement>(null);
  const [position, setPosition] = useState({ left: 0, top: 0 });

  useLayoutEffect(() => {
    if (!open || !ref.current) return;
    // Keep the menu inside the window.
    const { width, height } = ref.current.getBoundingClientRect();
    setPosition({
      left: Math.max(4, Math.min(open.x, window.innerWidth - width - 4)),
      top: Math.max(4, Math.min(open.y, window.innerHeight - height - 4)),
    });
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onKey = (event: KeyboardEvent) => event.key === "Escape" && close();
    const onDown = (event: MouseEvent) => {
      if (!ref.current?.contains(event.target as Node)) close();
    };
    window.addEventListener("keydown", onKey, true);
    window.addEventListener("mousedown", onDown, true);
    window.addEventListener("blur", close);
    return () => {
      window.removeEventListener("keydown", onKey, true);
      window.removeEventListener("mousedown", onDown, true);
      window.removeEventListener("blur", close);
    };
  }, [open, close]);

  if (!open) return null;
  return (
    <div ref={ref} className="menu" style={position} role="menu">
      {open.items.map((item, index) => {
        if ("separator" in item) return <hr key={index} />;
        if ("heading" in item) return <div key={index} className="heading">{item.heading}</div>;
        return (
          <button
            key={index}
            role="menuitem"
            disabled={item.disabled}
            onClick={() => {
              close();
              item.action();
            }}
          >
            <span className="row" style={{ gap: 6 }}>
              {item.checked !== undefined && <span className="check">{item.checked ? "✓" : ""}</span>}
              {item.label}
            </span>
            {item.hint && <span className="faint">{item.hint}</span>}
          </button>
        );
      })}
    </div>
  );
}
