import { useEffect, useState } from 'react'
import AdminPage from './pages/AdminPage'
import PairPage from './pages/PairPage'
import DeviceApp from './pages/DeviceApp'

type Route = 'admin' | 'pair' | 'app'

function routeFromLocation(): Route {
  const path = location.pathname.replace(/\/+$/, '')
  if (path === '/admin') return 'admin'
  if (path === '/pair') return 'pair'
  return 'app'
}

export default function App() {
  const [route, setRoute] = useState<Route>(routeFromLocation)

  useEffect(() => {
    const onPop = () => setRoute(routeFromLocation())
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [])

  // A single instance serves three surfaces: the loopback management page, the pairing entry
  // and the paired device application. They are separated by path, not by host.
  if (route === 'admin') return <AdminPage />
  if (route === 'pair') return <PairPage />
  return <DeviceApp />
}
