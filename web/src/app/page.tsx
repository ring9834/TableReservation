import Link from "next/link";

const screens = [
  { href: "/book/", label: "Book a table" },
  { href: "/manage/", label: "Manage a booking" },
  { href: "/demo-login/", label: "Demo login" },
  { href: "/host/", label: "Host" },
  { href: "/presenter/", label: "Presenter" },
];

export default function Home() {
  return (
    <main>
      <h1>Golden Dragon (demo)</h1>
      <ul>
        {screens.map((screen) => (
          <li key={screen.href}>
            <Link href={screen.href}>{screen.label}</Link>
          </li>
        ))}
      </ul>
    </main>
  );
}
