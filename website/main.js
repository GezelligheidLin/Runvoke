const root = document.documentElement
const themeToggle = document.querySelector('[data-theme-toggle]')
const themeColor = document.querySelector('meta[name="theme-color"]')
const menuToggle = document.querySelector('[data-menu-toggle]')
const mobileNav = document.querySelector('[data-mobile-nav]')
const header = document.querySelector('[data-header]')

const savedTheme = localStorage.getItem('runvoke-website-theme')
const initialTheme = savedTheme || (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')

function setTheme(theme) {
  root.dataset.theme = theme
  localStorage.setItem('runvoke-website-theme', theme)
  themeToggle?.setAttribute('aria-pressed', String(theme === 'dark'))
  themeToggle?.setAttribute('aria-label', theme === 'dark' ? '切换到浅色主题' : '切换到深色主题')
  themeToggle?.setAttribute('title', theme === 'dark' ? '切换到浅色主题' : '切换到深色主题')
  themeColor?.setAttribute('content', theme === 'dark' ? '#171a18' : '#f7f5f1')
}

setTheme(initialTheme)

themeToggle?.addEventListener('click', () => {
  setTheme(root.dataset.theme === 'dark' ? 'light' : 'dark')
})

function closeMenu() {
  menuToggle?.setAttribute('aria-expanded', 'false')
  menuToggle?.setAttribute('aria-label', '打开导航菜单')
  mobileNav?.classList.remove('open')
}

menuToggle?.addEventListener('click', () => {
  const isOpen = menuToggle.getAttribute('aria-expanded') === 'true'
  menuToggle.setAttribute('aria-expanded', String(!isOpen))
  menuToggle.setAttribute('aria-label', isOpen ? '打开导航菜单' : '关闭导航菜单')
  mobileNav?.classList.toggle('open', !isOpen)
})

mobileNav?.querySelectorAll('a').forEach((link) => link.addEventListener('click', closeMenu))

document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && menuToggle?.getAttribute('aria-expanded') === 'true') {
    closeMenu()
    menuToggle.focus()
  }
})

window.addEventListener('resize', () => {
  if (window.innerWidth > 820) closeMenu()
}, { passive: true })

const updateHeader = () => header?.classList.toggle('scrolled', window.scrollY > 12)
updateHeader()
window.addEventListener('scroll', updateHeader, { passive: true })

const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)')
const revealItems = document.querySelectorAll('.reveal')
if ('IntersectionObserver' in window && !reducedMotion.matches) {
  const observer = new IntersectionObserver((entries) => {
    entries.forEach((entry) => {
      if (entry.isIntersecting) {
        entry.target.classList.add('visible')
        observer.unobserve(entry.target)
      }
    })
  }, { threshold: 0.12 })
  revealItems.forEach((item) => observer.observe(item))
} else {
  revealItems.forEach((item) => item.classList.add('visible'))
}

const clamp = (value, min, max) => Math.min(Math.max(value, min), max)
const heroParallax = document.querySelector('[data-parallax-hero]')
const heroGrid = document.querySelector('[data-parallax-grid]')
const mediaParallaxItems = document.querySelectorAll('[data-parallax-media]')
let parallaxFrame = 0

function updateParallax() {
  parallaxFrame = 0
  const motionDisabled = reducedMotion.matches || window.innerWidth <= 820

  if (motionDisabled) {
    heroParallax?.style.removeProperty('--hero-parallax')
    heroGrid?.style.removeProperty('--grid-parallax')
    mediaParallaxItems.forEach((item) => item.style.removeProperty('--media-parallax'))
    return
  }

  const heroOffset = clamp(window.scrollY * 0.045, 0, 30)
  heroParallax?.style.setProperty('--hero-parallax', `${heroOffset}px`)
  heroGrid?.style.setProperty('--grid-parallax', `${window.scrollY * 0.028}px`)

  const viewportCenter = window.innerHeight / 2
  mediaParallaxItems.forEach((item) => {
    const rect = item.getBoundingClientRect()
    if (rect.bottom < -120 || rect.top > window.innerHeight + 120) return
    const itemCenter = rect.top + rect.height / 2
    const offset = clamp((viewportCenter - itemCenter) * 0.045, -18, 18)
    item.style.setProperty('--media-parallax', `${offset}px`)
  })
}

function requestParallaxUpdate() {
  if (parallaxFrame) return
  parallaxFrame = window.requestAnimationFrame(updateParallax)
}

updateParallax()
window.addEventListener('scroll', requestParallaxUpdate, { passive: true })
window.addEventListener('resize', requestParallaxUpdate, { passive: true })
reducedMotion.addEventListener?.('change', requestParallaxUpdate)

const marqueeSection = document.querySelector('[data-marquee-section]')

if (marqueeSection) {
  const marqueeStates = Array.from(marqueeSection.querySelectorAll('[data-marquee]')).map((marquee) => {
    const runner = marquee.querySelector('[data-marquee-runner]')
    const group = marquee.querySelector('[data-marquee-group]')
    const clone = group.cloneNode(true)
    clone.setAttribute('aria-hidden', 'true')
    runner.append(clone)

    const isCategoryTrack = marquee.dataset.marquee === 'categories'
    return {
      runner,
      group,
      width: 0,
      position: 0,
      direction: isCategoryTrack ? 1 : -1,
      baseSpeed: isCategoryTrack ? 0.024 : 0.038,
      boostRatio: isCategoryTrack ? 0.55 : 1,
    }
  })

  let marqueeFrame = 0
  let lastMarqueeTime = 0
  let wheelBoost = 0
  let marqueeVisible = false

  function measureMarquees() {
    marqueeStates.forEach((state) => {
      const previousWidth = state.width
      state.width = state.group.getBoundingClientRect().width
      if (!state.width) return

      const requiredCopies = Math.ceil(window.innerWidth / state.width) + 2
      while (state.runner.children.length < requiredCopies) {
        const copy = state.group.cloneNode(true)
        copy.setAttribute('aria-hidden', 'true')
        state.runner.append(copy)
      }

      if (previousWidth) {
        state.position = (state.position / previousWidth) * state.width
      } else if (state.direction > 0) {
        state.position = -state.width
      }
    })
  }

  function renderMarquees(time) {
    const elapsed = Math.min(40, lastMarqueeTime ? time - lastMarqueeTime : 16.67)
    lastMarqueeTime = time
    wheelBoost *= Math.pow(0.91, elapsed / 16.67)

    marqueeStates.forEach((state) => {
      if (!state.width) return
      const speed = state.baseSpeed + wheelBoost * state.boostRatio
      state.position += state.direction * speed * elapsed

      if (state.direction < 0 && state.position <= -state.width) state.position += state.width
      if (state.direction > 0 && state.position >= 0) state.position -= state.width
      state.runner.style.transform = `translate3d(${state.position}px, 0, 0)`
    })

    marqueeFrame = window.requestAnimationFrame(renderMarquees)
  }

  function syncMarqueeState() {
    const shouldRun = marqueeVisible && !reducedMotion.matches && !document.hidden
    if (shouldRun && !marqueeFrame) {
      lastMarqueeTime = 0
      marqueeFrame = window.requestAnimationFrame(renderMarquees)
    } else if (!shouldRun && marqueeFrame) {
      window.cancelAnimationFrame(marqueeFrame)
      marqueeFrame = 0
    }
  }

  marqueeSection.addEventListener('wheel', (event) => {
    if (reducedMotion.matches) return
    const rawDelta = Math.abs(event.deltaY) >= Math.abs(event.deltaX) ? event.deltaY : event.deltaX
    const deltaScale = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? window.innerHeight : 1
    wheelBoost = Math.min(0.24, wheelBoost + Math.abs(rawDelta * deltaScale) * 0.0012)
  }, { passive: true })

  const marqueeObserver = new IntersectionObserver(([entry]) => {
    marqueeVisible = entry.isIntersecting
    syncMarqueeState()
  }, { threshold: 0.05 })

  marqueeObserver.observe(marqueeSection)
  window.addEventListener('resize', measureMarquees, { passive: true })
  document.addEventListener('visibilitychange', syncMarqueeState)
  reducedMotion.addEventListener?.('change', syncMarqueeState)
  measureMarquees()
}

document.querySelector('[data-year]').textContent = String(new Date().getFullYear())
