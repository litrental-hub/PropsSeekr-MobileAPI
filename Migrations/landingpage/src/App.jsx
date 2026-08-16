import React, { useEffect, useRef, useState, useCallback } from 'react'
import './App.css'

/* ---------------------------------------------------------------------- */
/*  PropSeekr — "Find. Match. Close."                                     */
/*  Design tokens (from brief):                                           */
/*   navy #061B4D · blue #1E5EFF · teal #12C8A0                          */
/*   bg #FFFFFF · card #F5F7FA · ink #1A1A1A                             */
/*   display: Sora  ·  body: Inter                                       */
/* ---------------------------------------------------------------------- */

const FONT_IMPORT_ID = 'propseekr-fonts'

function useGoogleFonts() {
  useEffect(() => {
    if (document.getElementById(FONT_IMPORT_ID)) return
    const link = document.createElement('link')
    link.id = FONT_IMPORT_ID
    link.rel = 'stylesheet'
    link.href =
      'https://fonts.googleapis.com/css2?family=Sora:wght@500;600;700;800&family=Inter:wght@400;500;600;700&display=swap'
    document.head.appendChild(link)
  }, [])
}

/* ---------------------------------------------------------------------- */
/*  Scroll reveal hook                                                    */
/* ---------------------------------------------------------------------- */
function useReveal(threshold = 0.18) {
  const ref = useRef(null)
  const [visible, setVisible] = useState(false)
  useEffect(() => {
    const el = ref.current
    if (!el) return
    const io = new IntersectionObserver(
      ([entry]) => {
        if (entry.isIntersecting) {
          setVisible(true)
          io.disconnect()
        }
      },
      { threshold },
    )
    io.observe(el)
    return () => io.disconnect()
  }, [threshold])
  return [ref, visible]
}

function Reveal({ children, delay = 0, className = '', as: Tag = 'div', style = {} }) {
  const [ref, visible] = useReveal()
  return (
    <Tag
      ref={ref}
      className={className}
      style={{
        opacity: visible ? 1 : 0,
        transform: visible ? 'translateY(0)' : 'translateY(28px)',
        transition: `opacity .7s cubic-bezier(.2,.7,.2,1) ${delay}ms, transform .7s cubic-bezier(.2,.7,.2,1) ${delay}ms`,
        ...style,
      }}
    >
      {children}
    </Tag>
  )
}

/* ---------------------------------------------------------------------- */
/*  Animated counter                                                      */
/* ---------------------------------------------------------------------- */
function Counter({ target, suffix = '', duration = 1600 }) {
  const [ref, visible] = useReveal(0.4)
  const [val, setVal] = useState(0)
  useEffect(() => {
    if (!visible) return
    let raf
    const start = performance.now()
    const tick = (now) => {
      const p = Math.min(1, (now - start) / duration)
      const eased = 1 - Math.pow(1 - p, 3)
      setVal(Math.round(target * eased))
      if (p < 1) raf = requestAnimationFrame(tick)
    }
    raf = requestAnimationFrame(tick)
    return () => cancelAnimationFrame(raf)
  }, [visible, target, duration])
  return (
    <span ref={ref}>
      {val.toLocaleString('en-IN')}
      {suffix}
    </span>
  )
}

/* ---------------------------------------------------------------------- */
/*  Icons (inline SVG, stroke-based, brand-tinted)                        */
/* ---------------------------------------------------------------------- */
const Icon = {
  ai: (p) => (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" {...p}>
      <path d="M12 2v3M12 19v3M4.2 4.2l2.1 2.1M17.7 17.7l2.1 2.1M2 12h3M19 12h3M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1" strokeLinecap="round" />
      <circle cx="12" cy="12" r="4.2" />
    </svg>
  ),
  match: (p) => (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" {...p}>
      <circle cx="8" cy="12" r="4.5" />
      <circle cx="16" cy="12" r="4.5" />
    </svg>
  ),
  crm: (p) => (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" {...p}>
      <rect x="3.5" y="4" width="17" height="16" rx="2.4" />
      <path d="M3.5 9.5h17M8 4v-.5M16 4v-.5" strokeLinecap="round" />
      <path d="M7 13.5h4M7 16.5h7" strokeLinecap="round" />
    </svg>
  ),
  marketplace: (p) => (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" {...p}>
      <path d="M4 9l1.4-4.4A1.5 1.5 0 0 1 6.8 3.5h10.4a1.5 1.5 0 0 1 1.4 1.1L20 9" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M4 9h16v9.5A1.5 1.5 0 0 1 18.5 20h-13A1.5 1.5 0 0 1 4 18.5V9Z" strokeLinejoin="round" />
      <path d="M9 13v3M15 13v3" strokeLinecap="round" />
    </svg>
  ),
  visit: (p) => (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" {...p}>
      <rect x="3.5" y="4.5" width="17" height="16" rx="2.2" />
      <path d="M3.5 9.5h17M8 3v3M16 3v3" strokeLinecap="round" />
      <path d="M8 14l2.4 2.4L16 11" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  ),
  lock: (p) => (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" {...p}>
      <rect x="5" y="10.5" width="14" height="9.5" rx="2" />
      <path d="M8 10.5V7.5a4 4 0 0 1 8 0v3" strokeLinecap="round" />
      <circle cx="12" cy="15" r="1.4" />
    </svg>
  ),
  check: (p) => (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" {...p}>
      <path d="M4 12.5l5 5L20 6" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  ),
  cross: (p) => (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" {...p}>
      <path d="M6 6l12 12M18 6L6 18" strokeLinecap="round" />
    </svg>
  ),
  chevron: (p) => (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" {...p}>
      <path d="M6 9l6 6 6-6" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  ),
  whatsapp: (p) => (
    <svg viewBox="0 0 24 24" fill="currentColor" {...p}>
      <path d="M12 2.1c-5.46 0-9.9 4.44-9.9 9.9 0 1.75.46 3.4 1.26 4.83L2 22l5.29-1.32a9.86 9.86 0 0 0 4.71 1.2c5.46 0 9.9-4.44 9.9-9.9S17.46 2.1 12 2.1Zm0 18.02c-1.5 0-2.9-.4-4.12-1.1l-.3-.18-3.14.79.84-3.05-.2-.32a8.14 8.14 0 0 1-1.28-4.38c0-4.5 3.66-8.16 8.2-8.16 4.53 0 8.2 3.66 8.2 8.16 0 4.5-3.67 8.24-8.2 8.24Zm4.5-6.14c-.25-.12-1.46-.72-1.68-.8-.23-.08-.39-.12-.56.13-.16.24-.63.8-.78.96-.14.16-.29.18-.53.06-.25-.12-1.05-.39-2-1.23a7.5 7.5 0 0 1-1.38-1.72c-.15-.24-.02-.38.11-.5.11-.11.25-.29.37-.43.12-.14.16-.24.24-.4.08-.16.04-.3-.02-.42-.06-.12-.56-1.36-.77-1.86-.2-.49-.4-.42-.56-.43h-.48c-.16 0-.42.06-.64.3-.22.24-.84.82-.84 2s.86 2.32.98 2.48c.12.16 1.7 2.6 4.12 3.64.57.25 1.02.4 1.37.5.58.19 1.1.16 1.52.1.46-.07 1.46-.6 1.67-1.18.2-.58.2-1.08.14-1.18-.06-.1-.22-.16-.47-.28Z" />
    </svg>
  ),
  arrow: (p) => (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" {...p}>
      <path d="M5 12h14M13 6l6 6-6 6" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  ),
  android: (p) => (
    <svg viewBox="0 0 24 24" fill="currentColor" {...p}>
      <path d="M6.5 8.5v6.2a1 1 0 0 0 1 1h.7v3a1.3 1.3 0 0 0 2.6 0v-3h1.4v3a1.3 1.3 0 0 0 2.6 0v-3h.7a1 1 0 0 0 1-1V8.5H6.5Zm-1.8 0a1.15 1.15 0 0 0-1.15 1.15v3.9a1.15 1.15 0 1 0 2.3 0v-3.9A1.15 1.15 0 0 0 4.7 8.5Zm14.6 0a1.15 1.15 0 0 0-1.15 1.15v3.9a1.15 1.15 0 1 0 2.3 0v-3.9a1.15 1.15 0 0 0-1.15-1.15ZM8.3 4.9l-.86-1.5a.34.34 0 0 1 .59-.34l.9 1.55a5.7 5.7 0 0 1 4.14 0l.9-1.55a.34.34 0 1 1 .59.34l-.86 1.5A5 5 0 0 1 17 8.9H7a5 5 0 0 1 1.3-4Zm.66 2.05a.55.55 0 1 0 0-1.1.55.55 0 0 0 0 1.1Zm6.08 0a.55.55 0 1 0 0-1.1.55.55 0 0 0 0 1.1Z" />
    </svg>
  ),
  apple: (p) => (
    <svg viewBox="0 0 24 24" fill="currentColor" {...p}>
      <path d="M16.4 2c.12 1.02-.28 2.03-.9 2.76-.63.75-1.66 1.34-2.66 1.26-.14-1 .32-2.03.93-2.7C14.44 2.55 15.5 2.02 16.4 2Zm3.53 16.68c-.45 1.03-.99 2-1.7 2.9-.94 1.21-1.9 2.4-3.4 2.42-1.46.03-1.93-.88-3.6-.88-1.68 0-2.2.86-3.58.9-1.46.06-2.57-1.3-3.52-2.5-1.9-2.42-3.37-6.85-1.4-9.83a4.86 4.86 0 0 1 4.1-2.5c1.4-.03 2.72.95 3.58.95.85 0 2.46-1.17 4.15-1 .7.03 2.68.29 3.94 2.15-.1.06-2.35 1.4-2.33 4.11.03 3.24 2.75 4.32 2.78 4.34-.02.07-.44 1.54-1.02 2.94Z" />
    </svg>
  ),
}

/* ---------------------------------------------------------------------- */
/*  Data                                                                  */
/* ---------------------------------------------------------------------- */
const NAV_LINKS = [
  { label: 'Features', href: '#features' },
  { label: 'How it works', href: '#how' },
  { label: 'About', href: '#about' },
  { label: 'FAQ', href: '#faq' },
]

const PROBLEM_CARDS = [
  { n: '847', label: 'WhatsApp messages', sub: 'buried in a week' },
  { n: '30+', label: 'Broker groups', sub: 'muted, unread, forgotten' },
  { n: '0', label: 'Missed opportunities tracked', sub: 'no record, no recall' },
  { n: '~', label: 'Old listings recirculating', sub: 'same flat, fifth time' },
  { n: '12', label: 'Follow-ups forgotten', sub: 'this week alone' },
  { n: 'manual', label: 'Search, every single time', sub: 'scroll, ctrl+F, repeat' },
]

const FEATURES = [
  {
    icon: 'ai',
    title: 'AI property entry',
    desc: "Type it the way you'd tell a colleague. PropSeekr structures every detail automatically.",
  },
  {
    icon: 'match',
    title: 'Smart matching',
    desc: 'Buyers and properties pair themselves. No more scrolling to remember who wanted what.',
  },
  {
    icon: 'crm',
    title: 'Broker CRM',
    desc: 'Every property, requirement and follow-up organised in one place, not six chat threads.',
  },
  {
    icon: 'marketplace',
    title: 'Broker marketplace',
    desc: 'Browse live inventory from verified brokers across your network, always current.',
  },
  {
    icon: 'visit',
    title: 'Visit planner',
    desc: 'Schedule site visits, track progress, and know exactly what is pending today.',
  },
  {
    icon: 'lock',
    title: 'Secure client data',
    desc: 'Password protected records with controlled sharing — your clients stay yours.',
  },
]

const STEPS = [
  { t: 'Add property or requirement', d: 'Type it naturally, in your own words.' },
  { t: 'AI understands everything', d: 'Location, budget, type — structured in seconds.' },
  { t: 'Automatic matching', d: 'PropSeekr finds every relevant broker and buyer.' },
  { t: 'Broker confirmation', d: 'The other broker confirms interest on their end.' },
  { t: 'Unlock contact', d: 'Details unlock only once both sides confirm.' },
  { t: 'Schedule visit', d: 'Plan the site visit inside the app.' },
  { t: 'Close deal', d: 'Mark it closed. Move to the next one.' },
]

const COMPARISON = [
  ['WhatsApp chaos', 'Organised matching'],
  ['Old listings', 'Fresh, verified listings'],
  ['Manual search', 'Automatic matching'],
  ['Scattered notes', 'Broker CRM'],
  ['Random calls', 'Verified broker network'],
  ['No follow-up', 'Visit planner'],
]

const PULSE = [
  { label: 'Most active locality', value: 'Andheri West' },
  { label: 'Most requested budget', value: '₹80L – ₹1.2Cr' },
  { label: 'Most requested type', value: '2 BHK Apartment' },
  { label: 'New listings today', value: '184' },
  { label: 'New requirements today', value: '231' },
  { label: 'Top matching area', value: 'Thane West' },
]

const FAQS = [
  { q: 'Will PropSeekr replace brokers?', a: 'No. PropSeekr removes the manual grind — searching, tracking, following up — so you can spend that time closing. The broker relationship stays exactly where it belongs: with you.' },
  { q: 'Can I continue using WhatsApp?', a: "Yes. PropSeekr doesn't ask you to leave WhatsApp — it structures what's already happening there so nothing gets buried again." },
  { q: 'How are contacts unlocked?', a: 'Contact details unlock only after both brokers confirm interest in a match, keeping every conversation intentional.' },
  { q: 'Do I need technical knowledge?', a: 'None. If you can send a WhatsApp message, you can use PropSeekr.' },
  { q: 'Which cities are supported?', a: 'PropSeekr is live across Mumbai Metropolitan Region broker networks, with more cities opening through early access.' },
  { q: 'Is my client data secure?', a: 'Every record is password protected with controlled sharing — your client list is never visible to anyone outside your confirmed matches.' },
]

/* ---------------------------------------------------------------------- */
/*  Signature hero animation: WhatsApp bubbles -> structured match cards  */
/* ---------------------------------------------------------------------- */
const HERO_STAGES = ['chat', 'structuring', 'matches', 'connected', 'closed']

function HeroPhone() {
  const [stage, setStage] = useState(0)
  useEffect(() => {
    const id = setInterval(() => setStage((s) => (s + 1) % HERO_STAGES.length), 2200)
    return () => clearInterval(id)
  }, [])
  const label = ['Property added', 'AI structures data', '7 matches found', 'Broker connected', 'Deal closed'][stage]

  return (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center' }}>
      <div
        style={{
          width: 280,
          height: 560,
          borderRadius: 40,
          background: '#061B4D',
          padding: 12,
          boxShadow: '0 30px 60px -20px rgba(6,27,77,0.35)',
          position: 'relative',
        }}
      >
        <div
          style={{
            width: '100%',
            height: '100%',
            borderRadius: 30,
            background: '#FFFFFF',
            overflow: 'hidden',
            position: 'relative',
            display: 'flex',
            flexDirection: 'column',
          }}
        >
          <div style={{ height: 28, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
            <div style={{ width: 70, height: 5, borderRadius: 3, background: '#E3E7EF' }} />
          </div>

          <div
            style={{
              padding: '10px 16px',
              display: 'flex',
              alignItems: 'center',
              gap: 8,
              borderBottom: '1px solid #EEF1F6',
              flexShrink: 0,
            }}
          >
            <div
              style={{
                width: 26,
                height: 26,
                borderRadius: 8,
                background: 'linear-gradient(135deg,#1E5EFF,#12C8A0)',
              }}
            />
            <span style={{ fontFamily: 'Sora, sans-serif', fontWeight: 700, fontSize: 13, color: '#061B4D' }}>
              PropSeekr
            </span>
          </div>

          <div style={{ flex: 1, position: 'relative', padding: 14 }}>
            <HeroStageContent stage={HERO_STAGES[stage]} />
          </div>
        </div>
      </div>

      <div style={{ marginTop: 22, display: 'flex', gap: 7 }}>
        {HERO_STAGES.map((s, i) => (
          <div
            key={s}
            style={{
              width: i === stage ? 22 : 7,
              height: 7,
              borderRadius: 4,
              background: i === stage ? '#1E5EFF' : '#D7DEEA',
              transition: 'all .35s ease',
            }}
          />
        ))}
      </div>
      <div
        key={label}
        style={{
          marginTop: 10,
          fontFamily: 'Inter, sans-serif',
          fontWeight: 600,
          fontSize: 13,
          color: '#061B4D',
          animation: 'ps-fadeup .4s ease',
        }}
      >
        {label}
      </div>
    </div>
  )
}

function HeroStageContent({ stage }) {
  if (stage === 'chat') {
    const bubbles = [
      { me: false, text: '2bhk andheri west, budget 90L?' },
      { me: true, text: 'yes available, sea facing' },
      { me: false, text: 'send details pls' },
    ]
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8, animation: 'ps-fadein .4s ease' }}>
        {bubbles.map((b, i) => (
          <div
            key={i}
            style={{
              alignSelf: b.me ? 'flex-end' : 'flex-start',
              background: b.me ? '#DCF8E9' : '#F0F2F6',
              color: '#1A1A1A',
              padding: '7px 10px',
              borderRadius: 12,
              fontSize: 11.5,
              fontFamily: 'Inter, sans-serif',
              maxWidth: '78%',
              animation: `ps-pop .35s ease ${i * 0.12}s both`,
            }}
          >
            {b.text}
          </div>
        ))}
      </div>
    )
  }
  if (stage === 'structuring') {
    const rows = [
      ['Type', '2 BHK Apartment'],
      ['Locality', 'Andheri West'],
      ['Budget', '₹90,00,000'],
      ['Facing', 'Sea facing'],
    ]
    return (
      <div style={{ animation: 'ps-fadein .4s ease' }}>
        <div style={{ fontSize: 10.5, color: '#7A8399', fontFamily: 'Inter, sans-serif', marginBottom: 8, display: 'flex', alignItems: 'center', gap: 6 }}>
          <span style={{ width: 6, height: 6, borderRadius: 3, background: '#12C8A0', display: 'inline-block' }} />
          AI structuring…
        </div>
        <div style={{ background: '#F5F7FA', borderRadius: 12, padding: 10 }}>
          {rows.map(([k, v], i) => (
            <div
              key={k}
              style={{
                display: 'flex',
                justifyContent: 'space-between',
                padding: '6px 0',
                borderBottom: i < rows.length - 1 ? '1px solid #E7EBF2' : 'none',
                fontFamily: 'Inter, sans-serif',
                fontSize: 11,
                animation: `ps-fadein .35s ease ${i * 0.1}s both`,
              }}
            >
              <span style={{ color: '#7A8399' }}>{k}</span>
              <span style={{ color: '#061B4D', fontWeight: 600 }}>{v}</span>
            </div>
          ))}
        </div>
      </div>
    )
  }
  if (stage === 'matches') {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: 7, animation: 'ps-fadein .4s ease' }}>
        <div style={{ fontSize: 10.5, color: '#7A8399', fontFamily: 'Inter, sans-serif', marginBottom: 2 }}>
          7 matches found
        </div>
        {[1, 2, 3].map((i) => (
          <div
            key={i}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 8,
              background: '#F5F7FA',
              borderRadius: 10,
              padding: '8px 9px',
              animation: `ps-pop .35s ease ${i * 0.1}s both`,
            }}
          >
            <div
              style={{
                width: 26,
                height: 26,
                borderRadius: '50%',
                background: 'linear-gradient(135deg,#1E5EFF,#12C8A0)',
                flexShrink: 0,
              }}
            />
            <div style={{ flex: 1 }}>
              <div style={{ fontSize: 10.5, fontWeight: 600, color: '#061B4D', fontFamily: 'Inter, sans-serif' }}>
                Broker #{i} — 94% match
              </div>
              <div style={{ fontSize: 9.5, color: '#7A8399', fontFamily: 'Inter, sans-serif' }}>Andheri West · 2 BHK</div>
            </div>
          </div>
        ))}
      </div>
    )
  }
  if (stage === 'connected') {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', height: '100%', animation: 'ps-fadein .4s ease' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <div style={{ width: 40, height: 40, borderRadius: '50%', background: '#E6F1FB', display: 'flex', alignItems: 'center', justifyContent: 'center', fontFamily: 'Sora,sans-serif', fontWeight: 700, color: '#1E5EFF', fontSize: 13 }}>A</div>
          <div style={{ width: 26, height: 2, background: '#12C8A0' }} />
          <div style={{ width: 40, height: 40, borderRadius: '50%', background: '#E1F5EE', display: 'flex', alignItems: 'center', justifyContent: 'center', fontFamily: 'Sora,sans-serif', fontWeight: 700, color: '#12C8A0', fontSize: 13 }}>B</div>
        </div>
        <div style={{ marginTop: 14, fontSize: 11.5, fontFamily: 'Inter, sans-serif', color: '#061B4D', fontWeight: 600 }}>
          Contact unlocked
        </div>
        <div style={{ marginTop: 3, fontSize: 10, fontFamily: 'Inter, sans-serif', color: '#7A8399' }}>
          Visit scheduled for Sat, 11 AM
        </div>
      </div>
    )
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', height: '100%', animation: 'ps-fadein .4s ease' }}>
      <div
        style={{
          width: 54,
          height: 54,
          borderRadius: '50%',
          background: '#12C8A0',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          animation: 'ps-pop .4s ease',
        }}
      >
        <Icon.check style={{ width: 26, height: 26, color: '#fff' }} />
      </div>
      <div style={{ marginTop: 12, fontSize: 12.5, fontFamily: 'Inter, sans-serif', color: '#061B4D', fontWeight: 700 }}>
        Deal closed
      </div>
      <div style={{ marginTop: 3, fontSize: 10, fontFamily: 'Inter, sans-serif', color: '#7A8399' }}>
        Commission recorded
      </div>
    </div>
  )
}

/* ---------------------------------------------------------------------- */
/*  Section: What is PropSeekr — connection diagram                       */
/* ---------------------------------------------------------------------- */
function ConnectionDiagram() {
  const nodes = [
    { label: 'Broker A', sub: 'lists a property' },
    { label: 'Property', sub: 'structured by AI' },
    { label: 'AI matching', sub: 'finds the fit' },
    { label: 'Broker B', sub: 'has a buyer' },
    { label: 'Deal closed', sub: 'both earn' },
  ]
  return (
    <div
      style={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        flexWrap: 'wrap',
        gap: 0,
      }}
    >
      {nodes.map((n, i) => (
        <React.Fragment key={n.label}>
          <Reveal delay={i * 90}>
            <div
              style={{
                background: i === 4 ? '#061B4D' : '#F5F7FA',
                borderRadius: 16,
                padding: '18px 20px',
                minWidth: 128,
                textAlign: 'center',
              }}
            >
              <div
                style={{
                  fontFamily: 'Sora, sans-serif',
                  fontWeight: 700,
                  fontSize: 14.5,
                  color: i === 4 ? '#fff' : '#061B4D',
                }}
              >
                {n.label}
              </div>
              <div
                style={{
                  fontFamily: 'Inter, sans-serif',
                  fontSize: 11.5,
                  marginTop: 4,
                  color: i === 4 ? '#B9C6EA' : '#7A8399',
                }}
              >
                {n.sub}
              </div>
            </div>
          </Reveal>
          {i < nodes.length - 1 && (
            <div style={{ padding: '0 10px', color: '#C7CFDD', flexShrink: 0 }}>
              <Icon.arrow style={{ width: 20, height: 20 }} />
            </div>
          )}
        </React.Fragment>
      ))}
    </div>
  )
}

/* ---------------------------------------------------------------------- */
/*  Main component                                                        */
/* ---------------------------------------------------------------------- */
export default function PropSeekrLanding() {
  useGoogleFonts()
  const [navScrolled, setNavScrolled] = useState(false)
  const [mobileOpen, setMobileOpen] = useState(false)
  const [openFaq, setOpenFaq] = useState(0)

  useEffect(() => {
    const onScroll = () => setNavScrolled(window.scrollY > 12)
    window.addEventListener('scroll', onScroll)
    return () => window.removeEventListener('scroll', onScroll)
  }, [])

  const scrollTo = useCallback((href) => {
    setMobileOpen(false)
    const el = document.querySelector(href)
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' })
  }, [])

  return (
    <div
      style={{
        fontFamily: 'Inter, sans-serif',
        color: '#1A1A1A',
        background: '#FFFFFF',
        overflowX: 'hidden',
        minHeight: '100vh',
      }}
    >
      <style>{`
        @keyframes ps-fadein { from { opacity:0 } to { opacity:1 } }
        @keyframes ps-fadeup { from { opacity:0; transform:translateY(6px) } to { opacity:1; transform:translateY(0) } }
        @keyframes ps-pop { from { opacity:0; transform:scale(.92) } to { opacity:1; transform:scale(1) } }
        @keyframes ps-float { 0%,100% { transform:translateY(0) } 50% { transform:translateY(-10px) } }
        @keyframes ps-marquee { from { transform:translateX(0) } to { transform:translateX(-50%) } }
        .ps-btn-primary { transition: transform .18s ease, box-shadow .18s ease; }
        .ps-btn-primary:hover { transform: translateY(-2px); box-shadow: 0 14px 28px -10px rgba(30,94,255,0.45); }
        .ps-btn-secondary { transition: background .18s ease, border-color .18s ease; }
        .ps-btn-secondary:hover { background: #F5F7FA; }
        .ps-card { transition: transform .25s ease, box-shadow .25s ease; }
        .ps-card:hover { transform: translateY(-6px); box-shadow: 0 24px 48px -24px rgba(6,27,77,0.22); }
        .ps-navlink { position:relative; }
        .ps-navlink::after { content:''; position:absolute; left:0; bottom:-4px; width:0; height:2px; background:#1E5EFF; transition:width .2s ease; }
        .ps-navlink:hover::after { width:100%; }
        .ps-faq-btn:hover { background:#F5F7FA; }
        a, button { font-family: inherit; }
        input, button { font-family: inherit; }
      `}</style>

      <header
        style={{
          position: 'sticky',
          top: 0,
          zIndex: 50,
          background: navScrolled ? 'rgba(255,255,255,0.92)' : 'transparent',
          backdropFilter: navScrolled ? 'blur(10px)' : 'none',
          borderBottom: navScrolled ? '1px solid #EEF1F6' : '1px solid transparent',
          transition: 'all .25s ease',
        }}
      >
        <div
          style={{
            maxWidth: 1200,
            margin: '0 auto',
            padding: '16px 24px',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
          }}
        >
          <div style={{ display: 'flex', alignItems: 'center', gap: 9, cursor: 'pointer' }} onClick={() => window.scrollTo({ top: 0, behavior: 'smooth' })}>
            <div
              style={{
                width: 30,
                height: 30,
                borderRadius: 9,
                background: 'linear-gradient(135deg,#1E5EFF,#12C8A0)',
              }}
            />
            <span style={{ fontFamily: 'Sora, sans-serif', fontWeight: 800, fontSize: 19, color: '#061B4D' }}>
              PropSeekr
            </span>
          </div>

          <nav style={{ display: 'flex', gap: 34 }} className="ps-nav-desktop">
            {NAV_LINKS.map((l) => (
              <a
                key={l.href}
                href={l.href}
                onClick={(e) => {
                  e.preventDefault()
                  scrollTo(l.href)
                }}
                className="ps-navlink"
                style={{ fontSize: 14.5, fontWeight: 600, color: '#33394A', textDecoration: 'none' }}
              >
                {l.label}
              </a>
            ))}
          </nav>

          <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
            <button onClick={() => scrollTo('#final-cta')} className="ps-btn-primary" style={{ display: 'none' }} />
            <button
              onClick={() => scrollTo('#final-cta')}
              className="ps-btn-primary"
              style={{
                background: 'linear-gradient(135deg,#1E5EFF,#0F49D6)',
                color: '#fff',
                border: 'none',
                borderRadius: 999,
                padding: '10px 22px',
                fontSize: 14,
                fontWeight: 700,
                cursor: 'pointer',
              }}
            >
              Download app
            </button>
            <button
              onClick={() => setMobileOpen((o) => !o)}
              style={{
                display: 'none',
                background: 'none',
                border: '1px solid #E3E7EF',
                borderRadius: 10,
                width: 40,
                height: 40,
                cursor: 'pointer',
              }}
              className="ps-nav-toggle"
              aria-label="Toggle menu"
            >
              <div style={{ margin: '0 auto', width: 18 }}>
                <div style={{ height: 2, background: '#061B4D', marginBottom: 4, borderRadius: 1 }} />
                <div style={{ height: 2, background: '#061B4D', marginBottom: 4, borderRadius: 1 }} />
                <div style={{ height: 2, background: '#061B4D', borderRadius: 1 }} />
              </div>
            </button>
          </div>
        </div>

        {mobileOpen && (
          <div style={{ borderTop: '1px solid #EEF1F6', padding: '12px 24px', display: 'flex', flexDirection: 'column', gap: 4 }}>
            {NAV_LINKS.map((l) => (
              <a
                key={l.href}
                href={l.href}
                onClick={(e) => {
                  e.preventDefault()
                  scrollTo(l.href)
                }}
                style={{ padding: '10px 0', fontSize: 15, fontWeight: 600, color: '#33394A', textDecoration: 'none', borderBottom: '1px solid #F2F4F8' }}
              >
                {l.label}
              </a>
            ))}
          </div>
        )}

        <style>{`
          @media (max-width: 860px) {
            .ps-nav-desktop { display: none !important; }
            .ps-nav-toggle { display: flex !important; align-items:center; justify-content:center; }
          }
        `}</style>
      </header>

      <section
        style={{
          position: 'relative',
          maxWidth: 1200,
          margin: '0 auto',
          padding: '56px 24px 40px',
          display: 'grid',
          gridTemplateColumns: '1.05fr 0.95fr',
          gap: 40,
          alignItems: 'center',
        }}
        className="ps-hero-grid"
      >
        <div
          style={{
            position: 'absolute',
            top: -80,
            left: '-10%',
            width: 480,
            height: 480,
            borderRadius: '50%',
            background: 'radial-gradient(circle,#E6F1FB 0%,rgba(230,241,251,0) 70%)',
            zIndex: -1,
          }}
        />
        <div>
          <Reveal>
            <span
              style={{
                display: 'inline-flex',
                alignItems: 'center',
                gap: 7,
                background: '#E1F5EE',
                color: '#0F6E56',
                fontSize: 12.5,
                fontWeight: 700,
                padding: '6px 14px',
                borderRadius: 999,
                marginBottom: 22,
              }}
            >
              <Icon.whatsapp style={{ width: 13, height: 13 }} />
              India's first broker operating system
            </span>
          </Reveal>

          <Reveal delay={80}>
            <h1
              style={{
                fontFamily: 'Sora, sans-serif',
                fontWeight: 800,
                fontSize: 'clamp(34px, 4.6vw, 56px)',
                lineHeight: 1.08,
                color: '#061B4D',
                margin: 0,
                letterSpacing: '-0.01em',
              }}
            >
              Stop scrolling WhatsApp.
              <br />
              Start closing more deals.
            </h1>
          </Reveal>

          <Reveal delay={160}>
            <p
              style={{
                fontSize: 17,
                lineHeight: 1.65,
                color: '#4A5268',
                marginTop: 22,
                maxWidth: 520,
              }}
            >
              Manage inventory. Automatically match buyers and properties. Connect with verified brokers.
              Close deals faster.
            </p>
          </Reveal>

          <Reveal delay={240}>
            <div style={{ display: 'flex', gap: 14, marginTop: 32, flexWrap: 'wrap' }}>
              <button
                onClick={() => scrollTo('#final-cta')}
                className="ps-btn-primary"
                style={{
                  background: 'linear-gradient(135deg,#1E5EFF,#0F49D6)',
                  color: '#fff',
                  border: 'none',
                  borderRadius: 999,
                  padding: '15px 30px',
                  fontSize: 15.5,
                  fontWeight: 700,
                  cursor: 'pointer',
                  display: 'inline-flex',
                  alignItems: 'center',
                  gap: 8,
                }}
              >
                Download app <Icon.arrow style={{ width: 16, height: 16 }} />
              </button>
              <button
                className="ps-btn-secondary"
                style={{
                  background: '#fff',
                  color: '#061B4D',
                  border: '1.5px solid #DFE4EE',
                  borderRadius: 999,
                  padding: '15px 26px',
                  fontSize: 15.5,
                  fontWeight: 700,
                  cursor: 'pointer',
                }}
              >
                Watch 60 second demo
              </button>
            </div>
          </Reveal>

          <Reveal delay={320}>
            <div
              style={{
                display: 'flex',
                gap: 28,
                marginTop: 46,
                flexWrap: 'wrap',
                paddingTop: 28,
                borderTop: '1px solid #EEF1F6',
                maxWidth: 520,
              }}
            >
              {[
                ['96,000+', 'WhatsApp messages processed'],
                ['40,000+', 'Matches generated'],
                ['30+', 'Broker networks'],
              ].map(([n, l]) => (
                <div key={l}>
                  <div style={{ fontFamily: 'Sora, sans-serif', fontWeight: 800, fontSize: 22, color: '#061B4D' }}>{n}</div>
                  <div style={{ fontSize: 12.5, color: '#7A8399', marginTop: 3, maxWidth: 130 }}>{l}</div>
                </div>
              ))}
            </div>
          </Reveal>
        </div>

        <div style={{ display: 'flex', justifyContent: 'center', animation: 'ps-float 5s ease-in-out infinite' }}>
          <HeroPhone />
        </div>

        <style>{`
          @media (max-width: 900px) {
            .ps-hero-grid { grid-template-columns: 1fr !important; }
          }
        `}</style>
      </section>

      <section style={{ background: '#061B4D', padding: '88px 24px', marginTop: 60 }}>
        <div style={{ maxWidth: 1100, margin: '0 auto' }}>
          <Reveal>
            <h2
              style={{
                fontFamily: 'Sora, sans-serif',
                fontWeight: 700,
                fontSize: 'clamp(26px,3.4vw,38px)',
                color: '#fff',
                textAlign: 'center',
                margin: 0,
              }}
            >
              Every broker's day looks like this
            </h2>
          </Reveal>

          <div
            style={{
              marginTop: 48,
              display: 'grid',
              gridTemplateColumns: 'repeat(auto-fit,minmax(220px,1fr))',
              gap: 16,
            }}
          >
            {PROBLEM_CARDS.map((c, i) => (
              <Reveal key={c.label} delay={i * 60}>
                <div
                  style={{
                    background: 'rgba(255,255,255,0.05)',
                    border: '1px solid rgba(255,255,255,0.1)',
                    borderRadius: 16,
                    padding: '24px 22px',
                    height: '100%',
                  }}
                >
                  <div style={{ fontFamily: 'Sora, sans-serif', fontWeight: 800, fontSize: 26, color: '#12C8A0' }}>
                    {c.n}
                  </div>
                  <div style={{ fontSize: 14.5, fontWeight: 600, color: '#fff', marginTop: 8 }}>{c.label}</div>
                  <div style={{ fontSize: 12.5, color: '#8E97B3', marginTop: 4 }}>{c.sub}</div>
                </div>
              </Reveal>
            ))}
          </div>

          <Reveal delay={200}>
            <p
              style={{
                textAlign: 'center',
                marginTop: 52,
                fontFamily: 'Sora, sans-serif',
                fontSize: 'clamp(19px,2.2vw,26px)',
                fontWeight: 600,
                color: '#fff',
                maxWidth: 720,
                marginLeft: 'auto',
                marginRight: 'auto',
                lineHeight: 1.5,
              }}
            >
              Deals don't fail because buyers don't exist.
              <br />
              <span style={{ color: '#12C8A0' }}>Deals fail because the right brokers never find each other.</span>
            </p>
          </Reveal>

          <Reveal delay={260}>
            <div style={{ textAlign: 'center', marginTop: 30 }}>
              <button
                onClick={() => scrollTo('#how')}
                className="ps-btn-secondary"
                style={{
                  background: 'transparent',
                  border: '1.5px solid rgba(255,255,255,0.3)',
                  color: '#fff',
                  borderRadius: 999,
                  padding: '13px 26px',
                  fontSize: 14.5,
                  fontWeight: 700,
                  cursor: 'pointer',
                }}
              >
                See how it works
              </button>
            </div>
          </Reveal>
        </div>
      </section>

      <section id="about" style={{ padding: '96px 24px', maxWidth: 1160, margin: '0 auto' }}>
        <Reveal>
          <h2
            style={{
              fontFamily: 'Sora, sans-serif',
              fontWeight: 700,
              fontSize: 'clamp(26px,3.4vw,38px)',
              color: '#061B4D',
              textAlign: 'center',
              margin: 0,
            }}
          >
            Meet India's first broker operating system
          </h2>
        </Reveal>
        <Reveal delay={80}>
          <p style={{ textAlign: 'center', fontSize: 16.5, color: '#7A8399', marginTop: 16, maxWidth: 560, marginLeft: 'auto', marginRight: 'auto', lineHeight: 1.6 }}>
            Not another property portal. Not another CRM. PropSeekr combines broker CRM, AI matching engine,
            broker marketplace, visit planner and secure client management — in one place.
          </p>
        </Reveal>

        <div style={{ marginTop: 64, overflowX: 'auto', paddingBottom: 8 }}>
          <ConnectionDiagram />
        </div>
      </section>

      <section id="features" style={{ background: '#F5F7FA', padding: '96px 24px' }}>
        <div style={{ maxWidth: 1160, margin: '0 auto' }}>
          <Reveal>
            <h2
              style={{
                fontFamily: 'Sora, sans-serif',
                fontWeight: 700,
                fontSize: 'clamp(26px,3.4vw,38px)',
                color: '#061B4D',
                textAlign: 'center',
                margin: 0,
              }}
            >
              Everything you need. Nothing you don't.
            </h2>
          </Reveal>

          <div
            style={{
              marginTop: 52,
              display: 'grid',
              gridTemplateColumns: 'repeat(auto-fit,minmax(280px,1fr))',
              gap: 20,
            }}
          >
            {FEATURES.map((f, i) => {
              const IconEl = Icon[f.icon]
              return (
                <Reveal key={f.title} delay={i * 70}>
                  <div
                    className="ps-card"
                    style={{
                      background: '#fff',
                      borderRadius: 20,
                      padding: '30px 26px',
                      height: '100%',
                      boxShadow: '0 2px 10px -4px rgba(6,27,77,0.08)',
                    }}
                  >
                    <div
                      style={{
                        width: 48,
                        height: 48,
                        borderRadius: 14,
                        background: 'linear-gradient(135deg,#E6F1FB,#E1F5EE)',
                        display: 'flex',
                        alignItems: 'center',
                        justifyContent: 'center',
                        marginBottom: 18,
                      }}
                    >
                      <IconEl style={{ width: 24, height: 24, color: '#1E5EFF' }} />
                    </div>
                    <h3 style={{ fontFamily: 'Sora, sans-serif', fontWeight: 700, fontSize: 17.5, color: '#061B4D', margin: 0 }}>
                      {f.title}
                    </h3>
                    <p style={{ fontSize: 14.5, color: '#7A8399', marginTop: 9, lineHeight: 1.55 }}>{f.desc}</p>
                  </div>
                </Reveal>
              )
            })}
          </div>
        </div>
      </section>

      <section id="how" style={{ padding: '96px 24px', maxWidth: 1100, margin: '0 auto' }}>
        <Reveal>
          <h2
            style={{
              fontFamily: 'Sora, sans-serif',
              fontWeight: 700,
              fontSize: 'clamp(26px,3.4vw,38px)',
              color: '#061B4D',
              textAlign: 'center',
              margin: 0,
            }}
          >
            From WhatsApp message to closed deal
          </h2>
        </Reveal>

        <div style={{ marginTop: 60, position: 'relative' }}>
          <div
            style={{
              position: 'absolute',
              left: 19,
              top: 6,
              bottom: 6,
              width: 2,
              background: 'linear-gradient(#1E5EFF,#12C8A0)',
              opacity: 0.25,
            }}
            className="ps-timeline-line"
          />
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            {STEPS.map((s, i) => (
              <Reveal key={s.t} delay={i * 70}>
                <div style={{ display: 'flex', gap: 22, alignItems: 'flex-start', padding: '16px 0' }}>
                  <div
                    style={{
                      width: 40,
                      height: 40,
                      borderRadius: '50%',
                      background: '#fff',
                      border: '2px solid #1E5EFF',
                      color: '#1E5EFF',
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'center',
                      fontFamily: 'Sora, sans-serif',
                      fontWeight: 700,
                      fontSize: 14,
                      flexShrink: 0,
                      zIndex: 1,
                    }}
                  >
                    {i + 1}
                  </div>
                  <div>
                    <div style={{ fontFamily: 'Sora, sans-serif', fontWeight: 700, fontSize: 17, color: '#061B4D' }}>
                      {s.t}
                    </div>
                    <div style={{ fontSize: 14, color: '#7A8399', marginTop: 4 }}>{s.d}</div>
                  </div>
                </div>
              </Reveal>
            ))}
          </div>
        </div>
      </section>

      <section style={{ background: '#F5F7FA', padding: '96px 24px' }}>
        <div style={{ maxWidth: 760, margin: '0 auto' }}>
          <Reveal>
            <h2
              style={{
                fontFamily: 'Sora, sans-serif',
                fontWeight: 700,
                fontSize: 'clamp(26px,3.4vw,38px)',
                color: '#061B4D',
                textAlign: 'center',
                margin: 0,
              }}
            >
              Why brokers switch to PropSeekr
            </h2>
          </Reveal>

          <Reveal delay={100}>
            <div
              style={{
                marginTop: 46,
                background: '#fff',
                borderRadius: 20,
                overflow: 'hidden',
                boxShadow: '0 2px 10px -4px rgba(6,27,77,0.08)',
              }}
            >
              <div
                style={{
                  display: 'grid',
                  gridTemplateColumns: '1fr 1fr',
                  padding: '16px 24px',
                  background: '#061B4D',
                }}
              >
                <span style={{ fontFamily: 'Sora, sans-serif', fontWeight: 700, fontSize: 13.5, color: '#8E97B3' }}>
                  Traditional way
                </span>
                <span style={{ fontFamily: 'Sora, sans-serif', fontWeight: 700, fontSize: 13.5, color: '#12C8A0', textAlign: 'right' }}>
                  PropSeekr
                </span>
              </div>
              {COMPARISON.map(([bad, good], i) => (
                <div
                  key={bad}
                  style={{
                    display: 'grid',
                    gridTemplateColumns: '1fr 1fr',
                    padding: '16px 24px',
                    borderTop: '1px solid #EEF1F6',
                    alignItems: 'center',
                  }}
                >
                  <div style={{ display: 'flex', alignItems: 'center', gap: 9, fontSize: 14, color: '#9098AC' }}>
                    <Icon.cross style={{ width: 15, height: 15, color: '#D8577A', flexShrink: 0 }} />
                    {bad}
                  </div>
                  <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: 9, fontSize: 14, fontWeight: 600, color: '#061B4D', textAlign: 'right' }}>
                    {good}
                    <Icon.check style={{ width: 15, height: 15, color: '#12C8A0', flexShrink: 0 }} />
                  </div>
                </div>
              ))}
            </div>
          </Reveal>
        </div>
      </section>

      <section style={{ padding: '96px 24px', maxWidth: 1000, margin: '0 auto' }}>
        <div
          style={{
            display: 'grid',
            gridTemplateColumns: '0.7fr 1.3fr',
            gap: 48,
            alignItems: 'center',
          }}
          className="ps-founder-grid"
        >
          <Reveal>
            <div
              style={{
                width: '100%',
                aspectRatio: '1',
                borderRadius: 24,
                background: 'linear-gradient(135deg,#E6F1FB,#E1F5EE)',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
              }}
            >
              <span style={{ fontFamily: 'Sora, sans-serif', fontWeight: 800, fontSize: 42, color: '#1E5EFF' }}>PS</span>
            </div>
          </Reveal>
          <Reveal delay={100}>
            <div>
              <span style={{ fontSize: 12.5, fontWeight: 700, color: '#12C8A0', letterSpacing: '0.04em', textTransform: 'uppercase' }}>
                Founder story
              </span>
              <h2
                style={{
                  fontFamily: 'Sora, sans-serif',
                  fontWeight: 700,
                  fontSize: 'clamp(24px,3vw,32px)',
                  color: '#061B4D',
                  margin: '10px 0 18px',
                  lineHeight: 1.25,
                }}
              >
                Built by a broker. For brokers.
              </h2>
              <p style={{ fontSize: 15.5, color: '#4A5268', lineHeight: 1.75 }}>
                PropSeekr was born from five years of real brokerage experience. Every missed opportunity.
                Every buried WhatsApp message. Every lost commission. The software we always wished existed.
              </p>
              <button
                className="ps-btn-secondary"
                style={{
                  marginTop: 22,
                  background: '#fff',
                  border: '1.5px solid #DFE4EE',
                  color: '#061B4D',
                  borderRadius: 999,
                  padding: '12px 24px',
                  fontSize: 14,
                  fontWeight: 700,
                  cursor: 'pointer',
                }}
              >
                Meet the founder
              </button>
            </div>
          </Reveal>
        </div>
        <style>{`
          @media (max-width: 720px) {
            .ps-founder-grid { grid-template-columns: 1fr !important; }
          }
        `}</style>
      </section>

      <section style={{ background: '#061B4D', padding: '80px 24px' }}>
        <div
          style={{
            maxWidth: 1000,
            margin: '0 auto',
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fit,minmax(180px,1fr))',
            gap: 28,
            textAlign: 'center',
          }}
        >
          {[
            { target: 96000, suffix: '+', label: 'WhatsApp messages analysed' },
            { target: 40000, suffix: '+', label: 'Matches generated' },
            { target: 30, suffix: '+', label: 'Broker networks' },
            { target: 12000, suffix: '+', label: 'New opportunities' },
          ].map((s) => (
            <Reveal key={s.label}>
              <div
                style={{
                  fontFamily: 'Sora, sans-serif',
                  fontWeight: 800,
                  fontSize: 'clamp(28px,4vw,42px)',
                  color: '#fff',
                }}
              >
                <Counter target={s.target} suffix={s.suffix} />
              </div>
              <div style={{ fontSize: 13.5, color: '#8E97B3', marginTop: 8 }}>{s.label}</div>
            </Reveal>
          ))}
        </div>
      </section>

      <section style={{ padding: '96px 24px', maxWidth: 1100, margin: '0 auto' }}>
        <Reveal>
          <div style={{ textAlign: 'center' }}>
            <span
              style={{
                display: 'inline-flex',
                alignItems: 'center',
                gap: 7,
                background: '#E6F1FB',
                color: '#0C447C',
                fontSize: 12.5,
                fontWeight: 700,
                padding: '6px 14px',
                borderRadius: 999,
                marginBottom: 18,
              }}
            >
              <span style={{ width: 7, height: 7, borderRadius: 4, background: '#1E5EFF' }} />
              Live · updates daily
            </span>
            <h2
              style={{
                fontFamily: 'Sora, sans-serif',
                fontWeight: 700,
                fontSize: 'clamp(26px,3.4vw,38px)',
                color: '#061B4D',
                margin: 0,
              }}
            >
              Today's market pulse
            </h2>
          </div>
        </Reveal>

        <div
          style={{
            marginTop: 44,
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fit,minmax(240px,1fr))',
            gap: 16,
          }}
        >
          {PULSE.map((p, i) => (
            <Reveal key={p.label} delay={i * 60}>
              <div
                className="ps-card"
                style={{
                  background: '#F5F7FA',
                  borderRadius: 16,
                  padding: '22px 22px',
                  height: '100%',
                }}
              >
                <div style={{ fontSize: 12.5, color: '#7A8399', fontWeight: 600 }}>{p.label}</div>
                <div style={{ fontFamily: 'Sora, sans-serif', fontWeight: 700, fontSize: 19, color: '#061B4D', marginTop: 8 }}>
                  {p.value}
                </div>
              </div>
            </Reveal>
          ))}
        </div>
      </section>

      <section id="faq" style={{ background: '#F5F7FA', padding: '96px 24px' }}>
        <div style={{ maxWidth: 720, margin: '0 auto' }}>
          <Reveal>
            <h2
              style={{
                fontFamily: 'Sora, sans-serif',
                fontWeight: 700,
                fontSize: 'clamp(26px,3.4vw,38px)',
                color: '#061B4D',
                textAlign: 'center',
                margin: 0,
              }}
            >
              Questions, answered
            </h2>
          </Reveal>

          <div style={{ marginTop: 44, display: 'flex', flexDirection: 'column', gap: 12 }}>
            {FAQS.map((f, i) => {
              const open = openFaq === i
              return (
                <Reveal key={f.q} delay={i * 50}>
                  <div
                    style={{
                      background: '#fff',
                      borderRadius: 16,
                      overflow: 'hidden',
                      boxShadow: '0 2px 10px -4px rgba(6,27,77,0.06)',
                    }}
                  >
                    <button
                      className="ps-faq-btn"
                      onClick={() => setOpenFaq(open ? -1 : i)}
                      style={{
                        width: '100%',
                        background: 'transparent',
                        border: 'none',
                        padding: '20px 22px',
                        display: 'flex',
                        alignItems: 'center',
                        justifyContent: 'space-between',
                        cursor: 'pointer',
                        textAlign: 'left',
                      }}
                    >
                      <span style={{ fontFamily: 'Sora, sans-serif', fontWeight: 700, fontSize: 15.5, color: '#061B4D' }}>
                        {f.q}
                      </span>
                      <Icon.chevron
                        style={{
                          width: 18,
                          height: 18,
                          color: '#1E5EFF',
                          flexShrink: 0,
                          marginLeft: 14,
                          transform: open ? 'rotate(180deg)' : 'rotate(0)',
                          transition: 'transform .25s ease',
                        }}
                      />
                    </button>
                    <div
                      style={{
                        maxHeight: open ? 200 : 0,
                        opacity: open ? 1 : 0,
                        overflow: 'hidden',
                        transition: 'max-height .3s ease, opacity .25s ease',
                      }}
                    >
                      <p style={{ margin: 0, padding: '0 22px 20px', fontSize: 14.5, color: '#7A8399', lineHeight: 1.65 }}>
                        {f.a}
                      </p>
                    </div>
                  </div>
                </Reveal>
              )
            })}
          </div>
        </div>
      </section>

      <section id="final-cta" style={{ padding: '100px 24px' }}>
        <Reveal>
          <div
            style={{
              maxWidth: 900,
              margin: '0 auto',
              background: 'linear-gradient(135deg,#061B4D,#0F49D6)',
              borderRadius: 32,
              padding: '70px 40px',
              textAlign: 'center',
              position: 'relative',
              overflow: 'hidden',
            }}
          >
            <div
              style={{
                position: 'absolute',
                top: -60,
                right: -60,
                width: 260,
                height: 260,
                borderRadius: '50%',
                background: 'radial-gradient(circle,rgba(18,200,160,0.35),transparent 70%)',
              }}
            />
            <h2
              style={{
                fontFamily: 'Sora, sans-serif',
                fontWeight: 800,
                fontSize: 'clamp(28px,4vw,42px)',
                color: '#fff',
                margin: 0,
                position: 'relative',
              }}
            >
              Ready to close more deals?
            </h2>
            <p style={{ color: '#B9C6EA', fontSize: 16.5, marginTop: 14, position: 'relative' }}>
              Download PropSeekr today.
            </p>
            <div style={{ display: 'flex', gap: 14, justifyContent: 'center', marginTop: 34, flexWrap: 'wrap', position: 'relative' }}>
              <button
                className="ps-btn-primary"
                style={{
                  display: 'inline-flex',
                  alignItems: 'center',
                  gap: 9,
                  background: '#fff',
                  color: '#061B4D',
                  border: 'none',
                  borderRadius: 999,
                  padding: '15px 28px',
                  fontSize: 15,
                  fontWeight: 700,
                  cursor: 'pointer',
                }}
              >
                <Icon.android style={{ width: 18, height: 18 }} />
                Download for Android
              </button>
              <button
                className="ps-btn-secondary"
                style={{
                  display: 'inline-flex',
                  alignItems: 'center',
                  gap: 9,
                  background: 'transparent',
                  color: '#fff',
                  border: '1.5px solid rgba(255,255,255,0.35)',
                  borderRadius: 999,
                  padding: '15px 26px',
                  fontSize: 15,
                  fontWeight: 700,
                  cursor: 'not-allowed',
                  opacity: 0.85,
                }}
                disabled
              >
                <Icon.apple style={{ width: 17, height: 17 }} />
                Coming soon on iOS
              </button>
            </div>
          </div>
        </Reveal>
      </section>

      <footer style={{ borderTop: '1px solid #EEF1F6', padding: '48px 24px' }}>
        <div
          style={{
            maxWidth: 1200,
            margin: '0 auto',
            display: 'flex',
            justifyContent: 'space-between',
            flexWrap: 'wrap',
            gap: 24,
          }}
        >
          <div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 9 }}>
              <div style={{ width: 26, height: 26, borderRadius: 8, background: 'linear-gradient(135deg,#1E5EFF,#12C8A0)' }} />
              <span style={{ fontFamily: 'Sora, sans-serif', fontWeight: 800, fontSize: 17, color: '#061B4D' }}>
                PropSeekr
              </span>
            </div>
            <div style={{ fontSize: 13, color: '#9098AC', marginTop: 8 }}>Find. Match. Close.</div>
          </div>

          <div style={{ display: 'flex', gap: 44, flexWrap: 'wrap' }}>
            <div>
              <div style={{ fontSize: 12.5, fontWeight: 700, color: '#061B4D', marginBottom: 10 }}>Quick links</div>
              {['Privacy policy', 'Terms', 'Support'].map((l) => (
                <div key={l} style={{ fontSize: 13.5, color: '#7A8399', marginBottom: 8, cursor: 'pointer' }}>
                  {l}
                </div>
              ))}
            </div>
            <div>
              <div style={{ fontSize: 12.5, fontWeight: 700, color: '#061B4D', marginBottom: 10 }}>Contact</div>
              <div style={{ fontSize: 13.5, color: '#7A8399', marginBottom: 8 }}>hello@propseekr.com</div>
              <div style={{ fontSize: 13.5, color: '#7A8399' }}>Instagram · LinkedIn</div>
            </div>
          </div>
        </div>
        <div style={{ maxWidth: 1200, margin: '32px auto 0', paddingTop: 20, borderTop: '1px solid #F2F4F8', fontSize: 12.5, color: '#B0B6C6', textAlign: 'center' }}>
          © {new Date().getFullYear()} PropSeekr. All rights reserved.
        </div>
      </footer>
    </div>
  )
}
