/** What a page shows instead of its content while the tenant has its feature switched off. */
export function FeatureOff(props: { title: string; name: string }) {
  return (
    <div className="page">
      <h1>{props.title}</h1>
      <p className="banner">
        {props.name} is turned off for this tenant. An administrator can turn it back on in Settings.
      </p>
    </div>
  )
}
