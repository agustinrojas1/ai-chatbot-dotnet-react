const linkClass = 'font-medium text-whatsapp-teal underline-offset-2 hover:underline'

export function Footer() {
  return (
    <footer className="bg-whatsapp-input px-3 pb-2 text-center text-[11px] text-whatsapp-meta">
      Built by Agustín Rojas ·{' '}
      <a href="https://github.com/agustinrojas1" target="_blank" rel="noopener noreferrer" className={linkClass}>
        GitHub
      </a>{' '}
      ·{' '}
      <a
        href="https://www.linkedin.com/in/agustin-jorge-rojas/"
        target="_blank"
        rel="noopener noreferrer"
        className={linkClass}
      >
        LinkedIn
      </a>
    </footer>
  )
}
