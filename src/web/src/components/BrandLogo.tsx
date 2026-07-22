export function BrandLogo({ className }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 248 73"
      xmlns="http://www.w3.org/2000/svg"
      preserveAspectRatio="xMinYMid meet"
      className={className}
      aria-label="DMARC Analyzer .NET"
      role="img"
    >
      <rect x="1" y="0" width="70" height="70" rx="14" fill="#0D9488" />
      <rect
        x="14"
        y="20"
        width="44"
        height="30"
        rx="3"
        fill="none"
        stroke="white"
        strokeWidth="1.8"
      />
      <path
        d="M15 21.25L36 38L57 21.25"
        fill="none"
        stroke="white"
        strokeWidth="1.8"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
      <text
        x="88"
        y="48"
        fontFamily="'Helvetica Neue',Helvetica,Arial,sans-serif"
        fontSize="44"
        fontWeight="700"
        fill="#0F172A"
        letterSpacing="-1"
      >
        DMARC
      </text>
      <text
        x="90"
        y="69"
        fontFamily="'Helvetica Neue',Helvetica,Arial,sans-serif"
        fontSize="13"
        fontWeight="400"
        fill="#64748B"
        letterSpacing="4.5"
      >
        ANALYZER
      </text>
      <rect x="198" y="56" width="35" height="17" rx="3" fill="#0D9488" />
      <text
        x="215.5"
        y="68"
        fontFamily="'Helvetica Neue',Helvetica,Arial,sans-serif"
        fontSize="10.5"
        fontWeight="500"
        fill="white"
        textAnchor="middle"
      >
        .NET
      </text>
    </svg>
  )
}
