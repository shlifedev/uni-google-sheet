// Small inline icons, drawn for this app so it needs no icon font. 16px grid, currentColor unless they carry meaning.
import type { IconName } from "../lib/tree";

type Props = { size?: number; className?: string };

const svg = (size: number, className: string | undefined, children: React.ReactNode) => (
  <svg width={size} height={size} viewBox="0 0 16 16" fill="none" className={className} aria-hidden="true">
    {children}
  </svg>
);

export function NodeIcon({ name, size = 16 }: { name: IconName; size?: number }) {
  switch (name) {
    case "excel":
      return svg(
        size,
        undefined,
        <>
          <rect x="2" y="2" width="12" height="12" rx="2" fill="#1f7a45" />
          <path d="M5.2 5l5.6 6M10.8 5l-5.6 6" stroke="#fff" strokeWidth="1.5" strokeLinecap="round" />
        </>,
      );
    case "sheets":
      return svg(
        size,
        undefined,
        <>
          <path d="M3.5 1.5h6l3 3v10h-9z" fill="#1fa463" />
          <path d="M9.5 1.5v3h3" fill="#0f7a44" />
          <rect x="5" y="7" width="6" height="5" fill="#fff" />
          <path d="M5 9.5h6M8 7v5" stroke="#1fa463" strokeWidth="1" />
        </>,
      );
    case "drive":
      return svg(
        size,
        undefined,
        <>
          <path d="M5.6 2h4.8l4.1 7.1-2.4 4.1z" fill="#ffba00" />
          <path d="M5.6 2L1.5 9.1l2.4 4.1L8 6.1z" fill="#0f9d58" />
          <path d="M3.9 13.2h8.2l2.4-4.1H6.3z" fill="#4285f4" />
        </>,
      );
    case "folder":
      return svg(
        size,
        undefined,
        <path d="M1.5 4a1 1 0 011-1h3.6l1.4 1.5h6a1 1 0 011 1V12a1 1 0 01-1 1h-11a1 1 0 01-1-1z" fill="#d9a23a" />,
      );
    case "table":
      return svg(
        size,
        undefined,
        <>
          <rect x="2" y="3" width="12" height="10" rx="1.5" stroke="#4f7fd1" strokeWidth="1.3" />
          <path d="M2 6.5h12M6.5 6.5V13" stroke="#4f7fd1" strokeWidth="1.3" />
        </>,
      );
    case "config":
      return svg(
        size,
        undefined,
        <>
          <circle cx="8" cy="8" r="2.2" stroke="#7d8794" strokeWidth="1.4" />
          <path
            d="M8 1.8v2M8 12.2v2M1.8 8h2M12.2 8h2M3.6 3.6l1.4 1.4M11 11l1.4 1.4M3.6 12.4L5 11M11 5l1.4-1.4"
            stroke="#7d8794"
            strokeWidth="1.4"
            strokeLinecap="round"
          />
        </>,
      );
    case "enum":
      return svg(
        size,
        undefined,
        <>
          <rect x="2" y="2.5" width="12" height="11" rx="2" fill="#c08a10" />
          <path d="M5.5 5.5h5M5.5 8h5M5.5 10.5h5" stroke="#fff" strokeWidth="1.3" strokeLinecap="round" />
        </>,
      );
  }
}

export const Chevron = ({ size = 12, className }: Props) =>
  svg(size, className, <path d="M6 4l4 4-4 4" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />);

export const SearchIcon = ({ size = 13, className }: Props) =>
  svg(
    size,
    className,
    <>
      <circle cx="7" cy="7" r="4.5" stroke="currentColor" strokeWidth="1.6" />
      <path d="M10.5 10.5L14 14" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
    </>,
  );

export const ErrorIcon = ({ size = 14, className }: Props) =>
  svg(
    size,
    className,
    <>
      <circle cx="8" cy="8" r="6.5" fill="currentColor" />
      <path d="M5.8 5.8l4.4 4.4M10.2 5.8l-4.4 4.4" stroke="#fff" strokeWidth="1.6" strokeLinecap="round" />
    </>,
  );

export const WarningIcon = ({ size = 14, className }: Props) =>
  svg(
    size,
    className,
    <>
      <path d="M8 1.8l6.6 11.7H1.4z" fill="currentColor" strokeLinejoin="round" />
      <path d="M8 6v3.6" stroke="#fff" strokeWidth="1.6" strokeLinecap="round" />
      <circle cx="8" cy="11.6" r="0.9" fill="#fff" />
    </>,
  );

export const CloseIcon = ({ size = 12, className }: Props) =>
  svg(size, className, <path d="M4 4l8 8M12 4l-8 8" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />);

export const MenuIcon = ({ size = 14, className }: Props) =>
  svg(
    size,
    className,
    <>
      <circle cx="3.5" cy="8" r="1.3" fill="currentColor" />
      <circle cx="8" cy="8" r="1.3" fill="currentColor" />
      <circle cx="12.5" cy="8" r="1.3" fill="currentColor" />
    </>,
  );

export const PlusIcon = ({ size = 12, className }: Props) =>
  svg(size, className, <path d="M8 3v10M3 8h10" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />);

export const UpDownIcon = ({ size = 12, className }: Props) =>
  svg(
    size,
    className,
    <path d="M4 6l4-4 4 4M4 10l4 4 4-4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />,
  );

export const KeyIcon = ({ size = 11, className }: Props) =>
  svg(
    size,
    className,
    <>
      <circle cx="5" cy="8" r="3" stroke="currentColor" strokeWidth="1.6" />
      <path d="M8 8h6.5M12 8v2.5M14 8v2" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
    </>,
  );
